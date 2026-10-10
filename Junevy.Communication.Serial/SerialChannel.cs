using System.IO.Ports;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.Serial.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Serial;

/// <summary>
/// 串口客户端通道（设计文档第 8 节）。在 <see cref="StreamClientChannel"/> 之上实现串口传输：基于 <c>SerialPort.BaseStream</c>，不使用 <c>DataReceived</c> 事件。
/// 打开端口受 <see cref="SerialChannelConfig.OpenTimeout"/> 约束（驱动可能阻塞）；读取出错后由监督器按重连策略重新打开端口。
/// </summary>
/// <remarks>
/// 构造时校验并复制配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，调用方的配置对象不会被修改；运行行为只依赖构造时的快照。
/// 设计文档第 10 节未为串口列出握手超时与断开等待，因此握手不限时，断开时不等待。请求超时不重新打开端口（<c>ResetOnRequestTimeout</c> 为 false），
/// 迟到应答由 <c>LateReplyWindow</c> 处理。
/// </remarks>
public sealed class SerialChannel : StreamClientChannel, ISerialChannel
{
    private readonly SerialChannelConfig config;
    private readonly SerialChannelConfig portSettings;
    private readonly ISerialPortHandleFactory handleFactory;
    private readonly ILogger<SerialChannel> logger;
    private readonly object portSync = new object();
    private ISerialPortHandle? activeHandle;

    /// <summary>
    /// 创建串口通道，名称默认为 <see cref="SerialChannelConfig.PortName"/>。
    /// </summary>
    /// <param name="config">串口配置；构造时校验并复制。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、重连）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public SerialChannel(SerialChannelConfig config, ILogger<SerialChannel>? logger = null, ChannelComponents? components = null)
        : this(DefaultName(config), config, logger, components)
    {
    }

    /// <summary>
    /// 创建具有指定名称的串口通道（供通道工厂使用）。
    /// </summary>
    /// <param name="name">通道名称（与注册表中的名称一致）。</param>
    /// <param name="config">串口配置；构造时校验并复制。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、重连）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">名称或配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public SerialChannel(string name, SerialChannelConfig config, ILogger<SerialChannel>? logger = null, ChannelComponents? components = null)
        : this(name, config, logger, components, SerialPortHandle.DefaultFactory)
    {
    }

    // 测试用构造：注入端口工厂（假端口）。公开构造使用真实的 SerialPort。
    internal SerialChannel(string name, SerialChannelConfig config, ILogger<SerialChannel>? logger, ChannelComponents? components,
                           ISerialPortHandleFactory handleFactory)
        : base(name, BuildSettings(config), components, logger ?? NullLogger<SerialChannel>.Instance)
    {
        this.config = config;
        portSettings = CopyPortSettings(config);
        this.handleFactory = handleFactory ?? throw new ArgumentNullException(nameof(handleFactory));
        this.logger = logger ?? NullLogger<SerialChannel>.Instance;
    }

    /// <inheritdoc />
    public SerialChannelConfig Config => config;

    /// <summary>
    /// 串口没有连接级的半帧语义：未成帧的残余字节在超过 <c>PartialFrameTimeout</c> 后丢弃，连接保持（设计文档第 8 节）。
    /// </summary>
    protected override PartialFrameAction PartialFrameAction => PartialFrameAction.Discard;

    /// <summary>
    /// 打开端口（受 <c>OpenTimeout</c> 约束）。打开失败（端口不存在、被占用、拒绝访问）返回 <c>ConnectionClosed</c>，消息包含端口名与原始异常消息；
    /// 超时返回 <c>Timeout</c>，端口在驱动最终返回时释放；用户取消抛出 <see cref="OperationCanceledException"/>。
    /// 成功时清空接收缓冲（<c>DiscardInBuffer</c>）后返回数据流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时为已打开端口的数据流。</returns>
    protected override async Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
    {
        ISerialPortHandle handle;
        try
        {
            handle = handleFactory.Create(portSettings);
        }
        catch (Exception ex)
        {
            return OpenFailed(ex);
        }

        using TimeoutScope window = TimeoutScope.Start(portSettings.OpenTimeout, cancellationToken);
        Task opening = Task.Run(handle.Open);
        bool completed = await WaitForOpenAsync(opening, window.Token).ConfigureAwait(false);
        if (!completed || window.Token.IsCancellationRequested)
        {
            // 驱动可能在之后才返回：由后台在它完成时释放端口，不让端口没有所有者（计划 14.2）。
            ReleaseWhenOpened(opening, handle);
            cancellationToken.ThrowIfCancellationRequested();
            return OpenTimedOut();
        }

        if (opening.IsFaulted)
        {
            DisposeQuietly(handle);
            return OpenFailed(opening.Exception!.GetBaseException());
        }

        return ActivateHandle(handle);
    }

    /// <inheritdoc />
    protected override void AbortTransport()
    {
        ISerialPortHandle? handle;
        lock (portSync)
        {
            handle = activeHandle;
            activeHandle = null;
        }

        if (handle != null)
            DisposeQuietly(handle);
    }

    /// <inheritdoc />
    protected override string DescribeEndpoint() => $"{portSettings.PortName}@{portSettings.BaudRate}";

    // 打开成功：记为当前端口（残留的旧端口先释放），清空接收缓冲（计划 14.2），返回数据流。失败时释放刚打开的端口。
    private CommResult<Stream> ActivateHandle(ISerialPortHandle handle)
    {
        try
        {
            handle.DiscardInBuffer();
            Stream stream = handle.BaseStream;
            ISerialPortHandle? previous;
            lock (portSync)
            {
                previous = activeHandle;
                activeHandle = handle;
            }

            if (previous != null)
                DisposeQuietly(previous);

            return CommResult<Stream>.Success(stream);
        }
        catch (Exception ex)
        {
            DisposeQuietly(handle);
            return OpenFailed(ex);
        }
    }

    // 释放在超时或取消之后才完成的打开：无论成功或失败，都在驱动返回时关闭端口，并观察它的异常。
    private void ReleaseWhenOpened(Task opening, ISerialPortHandle handle)
    {
        _ = opening.ContinueWith(task =>
        {
            _ = task.Exception;
            DisposeQuietly(handle);
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    // 关闭端口。中止是尽力而为：忽略异常，可重复调用。
    private void DisposeQuietly(ISerialPortHandle handle)
    {
        try
        {
            handle.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Closing the serial port {Port} failed; the handle was abandoned.", portSettings.PortName);
        }
    }

    private CommResult<Stream> OpenFailed(Exception exception)
    {
        logger.LogWarning(exception, "Opening serial port {Port} failed.", portSettings.PortName);
        string message = $"Opening serial port {portSettings.PortName} failed: {exception.Message}";
        return CommResult<Stream>.Fail(message, CommErrorKind.ConnectionClosed, null, exception);
    }

    private CommResult<Stream> OpenTimedOut()
    {
        logger.LogWarning("Opening serial port {Port} did not complete within {Timeout} ms.", portSettings.PortName, portSettings.OpenTimeout);
        string message = $"Opening serial port {portSettings.PortName} did not complete within {portSettings.OpenTimeout} ms.";
        return CommResult<Stream>.Fail(message, CommErrorKind.Timeout);
    }

    // 等待打开完成，或计时 / 取消先到达。返回 true 表示打开已完成（成功或失败），false 表示被放弃。
    private static async Task<bool> WaitForOpenAsync(Task opening, CancellationToken token)
    {
        var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => interrupted.TrySetResult(true)))
        {
            Task finished = await Task.WhenAny(opening, interrupted.Task).ConfigureAwait(false);
            return ReferenceEquals(finished, opening);
        }
    }

    private static string DefaultName(SerialChannelConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return config.PortName;
    }

    // 校验配置并生成基类的运行参数（D5）。基类在构造期间读取并复制这些值，不保留对调用方对象的引用。
    // 设计文档第 10 节未为串口列出握手超时与断开等待，因此两者为 0（不限时 / 不等待）；请求超时不重建连接，迟到应答由 LateReplyWindow 处理。
    private static ClientChannelSettings BuildSettings(SerialChannelConfig config)
    {
        Validate(config);
        return new ClientChannelSettings
        {
            HandshakeTimeout = 0,
            SendTimeout = config.SendTimeout,
            RequestTimeout = config.RequestTimeout,
            LateReplyWindow = config.LateReplyWindow,
            IdleTimeout = config.IdleTimeout,
            PartialFrameTimeout = config.PartialFrameTimeout,
            DisconnectTimeout = 0,
            ReceiveBufferSize = config.ReadBufferSize,
            Framing = config.Framing,
            Correlation = config.Correlation,
            ResetOnRequestTimeout = false,
            Heartbeat = config.Heartbeat,
            Reconnect = config.Reconnect,
            ReceiveQueueCapacity = config.ReceiveQueueCapacity,
            QueueFullMode = config.QueueFullMode,
        };
    }

    // 只校验串口特有的参数（D5）。其余参数（超时的非负性、迟到窗口、队列容量、分帧 / 心跳 / 重连是否为 null）由基类校验并以同名属性报告。
    private static void Validate(SerialChannelConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.PortName))
            throw new ArgumentException("PortName must not be empty.", nameof(config));

        RequirePositive(config.BaudRate, nameof(SerialChannelConfig.BaudRate));
        if (config.DataBits < 5 || config.DataBits > 8)
            throw new ArgumentOutOfRangeException(nameof(SerialChannelConfig.DataBits), config.DataBits, "DataBits must be within [5, 8].");
        if (!Enum.IsDefined(typeof(Parity), config.Parity))
            throw new ArgumentOutOfRangeException(nameof(SerialChannelConfig.Parity), config.Parity, "Unknown parity.");
        if (!Enum.IsDefined(typeof(StopBits), config.StopBits) || config.StopBits == StopBits.None)
            throw new ArgumentOutOfRangeException(nameof(SerialChannelConfig.StopBits), config.StopBits,
                "StopBits must be One, Two or OnePointFive; SerialPort does not support None.");
        if (!Enum.IsDefined(typeof(Handshake), config.Handshake))
            throw new ArgumentOutOfRangeException(nameof(SerialChannelConfig.Handshake), config.Handshake, "Unknown handshake.");

        RequireEvenBufferSize(config.ReadBufferSize, nameof(SerialChannelConfig.ReadBufferSize));
        RequireEvenBufferSize(config.WriteBufferSize, nameof(SerialChannelConfig.WriteBufferSize));

        RequirePositive(config.OpenTimeout, nameof(SerialChannelConfig.OpenTimeout));
        RequirePositive(config.SendTimeout, nameof(SerialChannelConfig.SendTimeout));
        RequirePositive(config.RequestTimeout, nameof(SerialChannelConfig.RequestTimeout));
    }

    // 复制端口相关的参数（构造时的快照，D5）：之后修改调用方的对象不影响已创建的通道，每次打开都使用这份副本。
    private static SerialChannelConfig CopyPortSettings(SerialChannelConfig source)
        => new SerialChannelConfig
        {
            PortName = source.PortName,
            BaudRate = source.BaudRate,
            DataBits = source.DataBits,
            Parity = source.Parity,
            StopBits = source.StopBits,
            Handshake = source.Handshake,
            DtrEnable = source.DtrEnable,
            RtsEnable = source.RtsEnable,
            ReadBufferSize = source.ReadBufferSize,
            WriteBufferSize = source.WriteBufferSize,
            OpenTimeout = source.OpenTimeout,
            SendTimeout = source.SendTimeout,
        };

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must be positive.");
    }

    // SerialPort 对奇数的缓冲区大小在设置时抛出 IOException，因此在配置时拒绝（D5）。
    private static void RequireEvenBufferSize(int value, string name)
    {
        RequirePositive(value, name);
        if (value % 2 != 0)
            throw new ArgumentOutOfRangeException(name, value, "The buffer size must be even; SerialPort rejects odd sizes.");
    }
}
