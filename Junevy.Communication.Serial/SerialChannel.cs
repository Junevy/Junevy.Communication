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
/// 构造时校验配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，调用方的配置对象不会被修改。运行行为只依赖构造时的快照；<see cref="Config"/> 返回的是构造时传入的对象本身，之后修改它不影响已创建的通道。
/// 握手受 <see cref="SerialChannelConfig.HandshakeTimeout"/> 约束（超时关闭端口并返回 <c>Timeout</c>）；断开时在 <see cref="SerialChannelConfig.DisconnectTimeout"/> 内
/// 排空已收到的帧。请求超时不重新打开端口（<c>ResetOnRequestTimeout</c> 为 false），迟到应答由 <c>LateReplyWindow</c> 处理。
/// </remarks>
public sealed class SerialChannel : StreamClientChannel, ISerialChannel
{
    // 拒绝访问时两次打开之间的等待（毫秒）：驱动释放端口通常在数十毫秒内完成。
    private const int OpenRetryIntervalMilliseconds = 20;

    private readonly SerialChannelConfig config;
    private readonly SerialChannelConfig portSettings;
    private readonly ISerialPortHandleFactory handleFactory;
    private readonly ILogger<SerialChannel> logger;
    private readonly object portSync = new object();
    private ISerialPortHandle? activeHandle;

    /// <summary>
    /// 创建串口通道，名称默认为 <see cref="SerialChannelConfig.PortName"/>。
    /// </summary>
    /// <param name="config">串口配置；构造时校验，之后的修改不影响通道。</param>
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
    /// <param name="config">串口配置；构造时校验，之后的修改不影响通道。</param>
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
        : base(name, BuildSettings(config, components), components, logger ?? NullLogger<SerialChannel>.Instance)
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
    /// 打开端口（受 <c>OpenTimeout</c> 约束）。拒绝访问（<see cref="UnauthorizedAccessException"/>）可能是瞬时状态（驱动释放端口是异步的），
    /// 在 <c>OpenTimeout</c> 总时限内每 20 ms 重试一次；时限用尽仍被拒绝访问则返回 <c>ConnectionClosed</c>，消息说明已经重试。
    /// 其他打开失败（端口不存在、被占用）立即返回 <c>ConnectionClosed</c>，消息包含端口名与原始异常消息；
    /// 超时返回 <c>Timeout</c>，端口在驱动最终返回时释放；用户取消抛出 <see cref="OperationCanceledException"/>。
    /// 成功时清空接收缓冲（<c>DiscardInBuffer</c>）后返回数据流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时为已打开端口的数据流。</returns>
    protected override async Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
    {
        using TimeoutScope window = TimeoutScope.Start(portSettings.OpenTimeout, cancellationToken);
        int attempts = 0;
        UnauthorizedAccessException? denied = null;
        while (true)
        {
            // 拒绝访问之后时限用尽：报告 ConnectionClosed 并说明已经重试（而不是超时）。
            if (denied != null && window.Token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return OpenFailedAfterRetries(denied, attempts);
            }

            attempts++;
            ISerialPortHandle handle;
            try
            {
                handle = handleFactory.Create(portSettings);
            }
            catch (Exception ex)
            {
                return OpenFailed(ex);
            }

            Task opening = Task.Run(handle.Open);
            bool completed = await WaitForOpenAsync(opening, window.Token).ConfigureAwait(false);
            Exception? failure = completed && opening.IsFaulted ? opening.Exception!.GetBaseException() : null;

            // 拒绝访问即使在时限到达之后才返回，也按拒绝访问处理（已经重试过），而不是超时。其余情况下时限到达即超时。
            bool timedOut = !completed || (window.Token.IsCancellationRequested && failure is not UnauthorizedAccessException);
            if (timedOut)
            {
                // 驱动可能在之后才返回：由后台在它完成时释放端口，不让端口没有所有者（计划 14.2）。
                ReleaseWhenOpened(opening, handle);
                cancellationToken.ThrowIfCancellationRequested();

                // 之前的尝试已被拒绝访问，时限到达时本次尝试仍未返回：时限用尽仍被拒绝访问，报告 ConnectionClosed（已重试）。
                return !completed && denied != null ? OpenFailedAfterRetries(denied, attempts) : OpenTimedOut();
            }

            if (failure == null)
                return ActivateHandle(handle);

            DisposeQuietly(handle);
            if (failure is not UnauthorizedAccessException access)
                return OpenFailed(failure);

            // 拒绝访问可能是瞬时状态：驱动释放端口是异步的，刚关闭的端口可能在短时间内仍被占用。在 OpenTimeout 总时限内重试。
            denied = access;
            try
            {
                await Task.Delay(OpenRetryIntervalMilliseconds, window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户取消立即结束；时限用尽则由循环开头报告。
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
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

    private CommResult<Stream> OpenFailedAfterRetries(Exception exception, int attempts)
    {
        logger.LogWarning(exception, "Opening serial port {Port} failed after {Attempts} attempt(s).", portSettings.PortName, attempts);
        string message = $"Opening serial port {portSettings.PortName} failed after {attempts} attempt(s) within {portSettings.OpenTimeout} ms: {exception.Message}";
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
    // 请求超时不重建连接，迟到应答由 LateReplyWindow 处理（ResetOnRequestTimeout 固定为 false，设计文档第 10 节）。
    private static ClientChannelSettings BuildSettings(SerialChannelConfig config, ChannelComponents? components)
    {
        Validate(config, components);
        return new ClientChannelSettings
        {
            HandshakeTimeout = config.HandshakeTimeout,
            SendTimeout = config.SendTimeout,
            RequestTimeout = config.RequestTimeout,
            LateReplyWindow = config.LateReplyWindow,
            IdleTimeout = config.IdleTimeout,
            PartialFrameTimeout = config.PartialFrameTimeout,
            DisconnectTimeout = config.DisconnectTimeout,
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
    private static void Validate(SerialChannelConfig config, ChannelComponents? components)
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
        RequirePositive(config.HandshakeTimeout, nameof(SerialChannelConfig.HandshakeTimeout));
        RequirePositive(config.SendTimeout, nameof(SerialChannelConfig.SendTimeout));
        RequirePositive(config.RequestTimeout, nameof(SerialChannelConfig.RequestTimeout));
        RequireNonNegative(config.DisconnectTimeout, nameof(SerialChannelConfig.DisconnectTimeout));

        // 串口写入几乎总是成功：内置探测只判断写入，无法发现设备沉默，因此必须指定期望的应答（设计 5.4、D14）。
        // 代码级探测（HealthProbe 或 HealthProbeFactory）替代内置探测时不需要 ExpectedReply。
        bool suppliedProbe = components?.HealthProbe != null || components?.HealthProbeFactory != null;
        if (config.Heartbeat is { Enabled: true } heartbeat && !suppliedProbe && string.IsNullOrEmpty(heartbeat.ExpectedReply))
            throw new ArgumentException("The built-in heartbeat of a serial channel must set Heartbeat.ExpectedReply: a serial write almost always succeeds, "
                                        + "so the probe could not detect a silent device. Set ExpectedReply or supply ChannelComponents.HealthProbe or ChannelComponents.HealthProbeFactory.");
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

    private static void RequireNonNegative(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must not be negative.");
    }

    // SerialPort 对奇数的缓冲区大小在设置时抛出 IOException，因此在配置时拒绝（D5）。
    private static void RequireEvenBufferSize(int value, string name)
    {
        RequirePositive(value, name);
        if (value % 2 != 0)
            throw new ArgumentOutOfRangeException(name, value, "The buffer size must be even; SerialPort rejects odd sizes.");
    }
}
