using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels;

/// <summary>
/// 字节流客户端通道的公开基类（D12）。把任何以 <see cref="Stream"/> 表示的传输（TCP、串口、命名管道、蓝牙串口等）
/// 变成具备连接生命周期、请求/应答关联、心跳、握手钩子与自动重连的 <see cref="IClientChannel"/>。
/// 派生类实现打开与中止传输，并可覆写流包装（例如 TLS）、优雅关闭与半帧处理方式。
/// </summary>
/// <remarks>
/// 公开操作在释放之后抛出 <see cref="ObjectDisposedException"/>（<see cref="DisconnectAsync"/> 除外：释放之后直接返回）。
/// 状态不是 Connected 时，<see cref="SendAsync"/>、<see cref="RequestAsync"/> 与 <see cref="ReceiveAsync"/> 立即返回 <c>NotConnected</c>。
/// 同步 <see cref="Dispose"/> 最长等待 <c>DisconnectTimeout + 1000</c> 毫秒（D16）。
/// </remarks>
public abstract class StreamClientChannel : IClientChannel
{
    private readonly string name;
    private readonly ILogger logger;
    private readonly ConnectionStatistics statistics = new ConnectionStatistics();
    private readonly StreamClientOptions resolved;
    private readonly StreamConnectionDriver driver;
    private readonly ConnectionSupervisor supervisor;
    private int disposed;

    /// <summary>
    /// 创建字节流客户端通道（尚未连接）。构造时校验并复制配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，
    /// 调用方的配置对象不会被修改。
    /// </summary>
    /// <param name="name">通道名称。</param>
    /// <param name="settings">运行参数。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、重连）；可为 null。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">名称、配置或日志记录器为 null。</exception>
    /// <exception cref="ArgumentException">配置非法，例如启用心跳却没有探测的负载。</exception>
    protected StreamClientChannel(string name, ClientChannelSettings settings, ChannelComponents? components, ILogger logger)
    {
        this.name = name ?? throw new ArgumentNullException(nameof(name));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        resolved = new StreamClientOptions(settings, components, this);
        driver = new StreamConnectionDriver(resolved, this, OpenStreamAsync, SecureStreamAsync, AbortTransport, OnClosingAsync, () => PartialFrameAction,
                                            RaiseFrameReceived, () => DescribeEndpoint(), statistics, logger);
        supervisor = new ConnectionSupervisor(name, driver, resolved.ReconnectPolicy, resolved.ReconnectOnInitialFailure, statistics, logger);
        driver.Attach(supervisor);
        supervisor.StateChanged += OnSupervisorStateChanged;
    }

    /// <summary>
    /// 打开传输（含该传输自己的连接超时）。用户取消时抛出 <see cref="OperationCanceledException"/>；其他失败返回失败结果。
    /// 每次连接（包括每次重连）都会调用一次。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时为已打开的流。</returns>
    protected abstract Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 链路连通后、开始收发之前对流做安全包装（例如 TLS）。在 <c>HandshakeTimeout</c> 计时窗口内执行，与 <c>IConnectionInitializer</c> 共享同一时限；
    /// 超时或取消时基类调用 <see cref="AbortTransport"/>。默认原样返回。失败返回 Fail，用户取消抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <param name="stream"><see cref="OpenStreamAsync"/> 返回的流。</param>
    /// <param name="cancellationToken">取消令牌（在握手时限内有效）。</param>
    /// <returns>成功时为用于收发的流（可以是包装后的流）。</returns>
    protected virtual Task<CommResult<Stream>> SecureStreamAsync(Stream stream, CancellationToken cancellationToken)
        => Task.FromResult(CommResult<Stream>.Success(stream));

    /// <summary>
    /// 立即中止传输（例如销毁套接字、关闭端口）。可以在任意线程上重复调用。
    /// 连接关闭、超时、取消写出与停止时都会调用。
    /// </summary>
    protected abstract void AbortTransport();

    /// <summary>
    /// 优雅关闭前的动作（例如 TCP 的 Shutdown(Send)）。默认无操作。关闭时最多等待 DisconnectTimeout 毫秒。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>动作完成的任务。</returns>
    protected virtual Task OnClosingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 未成帧数据超过 PartialFrameTimeout 时的处理方式：返回 <c>Disconnect</c> 断开连接，返回 <c>Discard</c> 丢弃残余字节并继续。默认断开。
    /// 每次连接时读取。
    /// </summary>
    protected virtual PartialFrameAction PartialFrameAction => PartialFrameAction.Disconnect;

    /// <summary>
    /// 日志用的端点描述，例如 <c>"192.168.1.10:5000"</c> 或 <c>"COM3"</c>。
    /// </summary>
    /// <returns>端点描述。</returns>
    protected abstract string DescribeEndpoint();

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public ConnectionState State => supervisor.State;

    /// <inheritdoc />
    public bool IsConnected => State == ConnectionState.Connected;

    /// <inheritdoc />
    public ConnectionStatistics Statistics => statistics;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    /// <inheritdoc />
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await supervisor.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
            return Task.CompletedTask;

        return supervisor.DisconnectAsync(resolved.DisconnectTimeout, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await supervisor.WaitForConnectedAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        StreamChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        return await channel.SendAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                       CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        StreamChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult<byte[]>.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        return await channel.RequestAsync(payload, options, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        StreamChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult<byte[]>.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        return await channel.ReceiveAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同步释放：等待 <see cref="DisposeAsync"/> 完成，最长 <c>DisconnectTimeout + 1000</c> 毫秒（D16）。
    /// 超时后返回，释放在后台继续。在事件处理器内调用时不会死锁。
    /// </summary>
    public void Dispose()
    {
        int limit = resolved.DisconnectTimeout + 1000;
        Task disposing = DisposeAsync().AsTask();
        if (!disposing.Wait(limit))
            logger.LogWarning("Disposing the channel {Name} did not finish within {Timeout} ms; the release continues in the background.", name, limit);
    }

    /// <summary>
    /// 异步释放：关闭连接、停止重连与心跳、结束状态事件派发。幂等。
    /// 在状态事件处理器内调用时只发出停止信号，不等待状态事件派发结束，因此不会死锁。
    /// </summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return supervisor.DisposeAsync();
    }

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    // 只有 Connected 时才返回字节通道；连接正在关闭或重建时即使有旧的通道对象也返回 null。
    private StreamChannel? ConnectedChannel()
        => supervisor.State == ConnectionState.Connected ? driver.CurrentChannel : null;

    // 监督器的状态事件经此转发：发送者为通道本身，订阅者逐个隔离异常。
    private void OnSupervisorStateChanged(object? sender, ConnectionStateChangedEventArgs args)
        => LifecycleSupport.InvokeEach(StateChanged, this, args, logger, "StateChanged");

    // 连接的帧经路由派发给订阅者：发送者为通道本身，订阅者逐个隔离异常（计划 9.3）。
    private Task RaiseFrameReceived(FrameReceivedEventArgs args)
    {
        FrameRouter.RaiseToSubscribers(FrameReceived, this, args, logger);
        return Task.CompletedTask;
    }
}
