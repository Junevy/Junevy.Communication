using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 数据报连接的驱动（计划 15.2，仿照 <see cref="StreamConnectionDriver"/>）。打开 = 绑定传输（由 openTransport 完成）→ 握手时限内执行初始化器
/// （关联表先进入握手状态，积压 D8；接收循环在它之后启动）→ 结束握手 → 启动心跳。握手失败或超时则本次打开失败。
/// 与字节流驱动的差异：没有流包装（TLS）与优雅关闭动作；接收循环与发送由 <see cref="DatagramChannel"/> 管理。
/// </summary>
internal sealed class DatagramConnectionDriver : IConnectionDriver
{
    private readonly DatagramClientOptions options;
    private readonly Func<CancellationToken, Task<CommResult<IDatagramTransport>>> openTransport;
    private readonly Func<FrameReceivedEventArgs, Task> raise;
    private readonly Func<string> describeEndpoint;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private ConnectionSupervisor? supervisor;
    private volatile LiveConnection? current;

    /// <summary>
    /// 创建驱动（未绑定监督器，需随后调用 <see cref="Attach"/>）。
    /// </summary>
    /// <param name="options">已校验的运行参数。</param>
    /// <param name="openTransport">绑定传输（每次打开都调用，包括每次重连）；失败返回失败结果，用户取消抛出 <see cref="OperationCanceledException"/>。</param>
    /// <param name="raise">派发一帧 FrameReceived 的回调。</param>
    /// <param name="describeEndpoint">日志用的端点描述。</param>
    /// <param name="statistics">连接统计（跨代次累计）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public DatagramConnectionDriver(DatagramClientOptions options, Func<CancellationToken, Task<CommResult<IDatagramTransport>>> openTransport,
                                    Func<FrameReceivedEventArgs, Task> raise, Func<string> describeEndpoint, ConnectionStatistics statistics,
                                    ILogger logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.openTransport = openTransport ?? throw new ArgumentNullException(nameof(openTransport));
        this.raise = raise ?? throw new ArgumentNullException(nameof(raise));
        this.describeEndpoint = describeEndpoint ?? throw new ArgumentNullException(nameof(describeEndpoint));
        this.statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>当前已建立连接的数据报通道；没有连接时为 null。</summary>
    public DatagramChannel? CurrentChannel => current?.Channel;

    /// <summary>
    /// 绑定监督器（只能调用一次，在任何打开之前）。
    /// </summary>
    /// <param name="owner">监督器。</param>
    /// <exception cref="InvalidOperationException">已经绑定。</exception>
    public void Attach(ConnectionSupervisor owner)
    {
        if (owner == null)
            throw new ArgumentNullException(nameof(owner));
        if (supervisor != null)
            throw new InvalidOperationException("The connection driver is already attached to a supervisor.");

        supervisor = owner;
    }

    /// <inheritdoc />
    public async Task<CommResult> OpenAsync(CancellationToken cancellationToken)
    {
        ConnectionSupervisor owner = supervisor ?? throw new InvalidOperationException("The connection driver has not been attached to a supervisor.");
        long generation = owner.OpeningGeneration;

        CommResult<IDatagramTransport> opened = await openTransport(cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
            return opened.ToResult();

        IDatagramTransport transport = opened.Data!;

        // 握手时限从绑定之后开始计时。时限耗尽时中止传输，使挂起的初始化器结束。握手成功后释放窗口，之后不再中止连接。
        using TimeoutScope window = TimeoutScope.Start(options.HandshakeTimeout, cancellationToken, () => SafeAbort(transport));
        LiveConnection? live = null;
        try
        {
            live = CreateConnection(transport, new ConnectionAttempt(owner, generation));
            live.Channel.Start();

            CommResult handshake = await HandshakeAsync(live, window, cancellationToken).ConfigureAwait(false);
            if (!handshake.IsSuccess)
            {
                await StopConnectionAsync(live, 0).ConfigureAwait(false);
                return handshake;
            }

            window.Dispose();
            StartHeartbeat(live);
            current = live;
            logger.LogInformation("Datagram connection opened to {Endpoint}.", describeEndpoint());
            return CommResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (live != null)
                await StopConnectionAsync(live, 0).ConfigureAwait(false);
            else
                SafeAbort(transport);

            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Opening the datagram connection to {Endpoint} failed.", describeEndpoint());
            if (live != null)
                await StopConnectionAsync(live, 0).ConfigureAwait(false);
            else
                SafeAbort(transport);

            return CommResult.Fail("Opening the datagram connection failed.", CommErrorKind.Unspecified, null, ex);
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(DisconnectReason reason, int drainTimeout)
    {
        LiveConnection? live = current;
        current = null;
        if (live == null)
            return;

        logger.LogDebug("Closing the datagram connection to {Endpoint} ({Reason}).", describeEndpoint(), reason);
        await StopConnectionAsync(live, drainTimeout).ConfigureAwait(false);
    }

    // 创建一条连接的全部内部对象。关联表先进入握手状态（D8），之后才由 OpenAsync 启动接收循环，保证连上即发的数据报进入积压。
    private LiveConnection CreateConnection(IDatagramTransport transport, ConnectionAttempt attempt)
    {
        var table = new PendingRequestTable(options.Correlation, options.KeyExtractor, options.LateReplyWindow, logger);
        table.BeginHandshake();

        var router = new FrameRouter(table, options.QueueCapacity, options.QueueFullMode, raise, statistics, logger);
        var channel = new DatagramChannel(transport, options.CreateChannelSettings(), table, router, statistics, logger, attempt.Report);
        return new LiveConnection(transport, channel, router, attempt);
    }

    // 握手：执行初始化器（与握手时限共享窗口），结束握手并把积压转入派发队列，最后确认握手期间没有发生故障。
    private async Task<CommResult> HandshakeAsync(LiveConnection live, TimeoutScope window, CancellationToken cancellationToken)
    {
        if (options.Initializer != null)
        {
            CommResult initialized = await RunInitializerAsync(live.Channel, window, cancellationToken).ConfigureAwait(false);
            if (!initialized.IsSuccess)
                return initialized;
        }

        await live.Router.EndHandshakeAsync(cancellationToken).ConfigureAwait(false);

        // 握手期间的故障先记在尝试上（监督器此时仍处于 Connecting），这里转换为打开失败，避免把已断开的连接报告为 Connected。
        if (live.Attempt.Faulted)
            return CommResult.Fail("The connection was lost during the handshake.", CommErrorKind.ConnectionClosed);

        return CommResult.Success();
    }

    // 在握手时限内执行初始化器。时限耗尽或取消后不再等待初始化器（它可能忽略取消令牌），但会观察它的异常。
    private async Task<CommResult> RunInitializerAsync(DatagramChannel channel, TimeoutScope window, CancellationToken cancellationToken)
    {
        Task<CommResult> work = StartInitializer(new DatagramHandshakeView(channel), window.Token);
        if (!await WaitForStepAsync(work, window.Token).ConfigureAwait(false))
        {
            ObserveAbandoned(work);
            cancellationToken.ThrowIfCancellationRequested();
            return TimeoutResult();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (work.IsCanceled || window.IsTimedOut)
            return TimeoutResult();

        if (work.IsFaulted)
        {
            Exception? cause = work.Exception?.InnerException ?? work.Exception;
            logger.LogWarning(cause, "The connection initializer threw an exception.");
            return CommResult.Fail("The connection initializer threw an exception.", CommErrorKind.Unspecified, null, cause);
        }

        return work.Result;
    }

    // 等待一个握手步骤完成，或握手时限耗尽、用户取消。返回 true 表示步骤已完成（成功、失败或取消均可），false 表示步骤被放弃。
    private static async Task<bool> WaitForStepAsync(Task work, CancellationToken windowToken)
    {
        var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (windowToken.Register(() => interrupted.TrySetResult(true)))
        {
            Task finished = await Task.WhenAny(work, interrupted.Task).ConfigureAwait(false);
            return ReferenceEquals(finished, work);
        }
    }

    private CommResult TimeoutResult()
        => CommResult.Fail($"The connection handshake did not complete within {options.HandshakeTimeout} ms.", CommErrorKind.Timeout);

    // 同步抛出的异常转换为已完成的故障任务，统一由 RunInitializerAsync 处理。
    private Task<CommResult> StartInitializer(IByteChannel view, CancellationToken token)
    {
        try
        {
            return options.Initializer!.InitializeAsync(view, token);
        }
        catch (Exception ex)
        {
            return Task.FromException<CommResult>(ex);
        }
    }

    private static void ObserveAbandoned(Task work)
    {
        work.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    // 心跳与空闲监视只在握手完成后启动；使用同一个尝试对象报告死亡。
    private void StartHeartbeat(LiveConnection live)
    {
        if (!options.Heartbeat.Enabled && options.IdleTimeout <= 0)
            return;

        ConnectionAttempt attempt = live.Attempt;
        var monitor = new HeartbeatMonitor(options.HealthProbe, options.Heartbeat, options.IdleTimeout, statistics,
                                           reason => attempt.Report(reason, null), logger);
        live.Heartbeat = monitor;
        monitor.Start();
    }

    // 停止一条连接。先分离尝试（之后的故障不再报告），再停止心跳，最后停止数据报通道（它会中止传输）。
    private async Task StopConnectionAsync(LiveConnection live, int drainTimeout)
    {
        live.Attempt.Detach();

        if (live.Heartbeat != null)
            await live.Heartbeat.StopAsync().ConfigureAwait(false);

        try
        {
            await live.Channel.StopAsync(drainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stopping the datagram channel failed; the transport is aborted.");
            SafeAbort(live.Transport);
        }
    }

    private void SafeAbort(IDatagramTransport transport)
    {
        try
        {
            transport.Abort();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aborting the datagram transport failed.");
        }
    }

    /// <summary>一条已打开的数据报连接及其内部对象。</summary>
    private sealed class LiveConnection
    {
        public LiveConnection(IDatagramTransport transport, DatagramChannel channel, FrameRouter router, ConnectionAttempt attempt)
        {
            Transport = transport;
            Channel = channel;
            Router = router;
            Attempt = attempt;
        }

        public IDatagramTransport Transport { get; }

        public DatagramChannel Channel { get; }

        public FrameRouter Router { get; }

        public ConnectionAttempt Attempt { get; }

        public HeartbeatMonitor? Heartbeat { get; set; }
    }

    /// <summary>
    /// 握手期间交给初始化器的字节通道视图：绑定到这一条新连接，不检查 State（此时尚未 Connected）。
    /// 收发的默认目标为定向模式的远端；非定向模式下发送与请求返回 InvalidRequest。握手期间不派发事件（未认领的数据报进入积压，D8）。
    /// </summary>
    private sealed class DatagramHandshakeView : IByteChannel
    {
        private readonly DatagramChannel channel;

        public DatagramHandshakeView(DatagramChannel channel)
        {
            this.channel = channel;
        }

        /// <inheritdoc />
        public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => channel.SendAsync(payload, channel.DirectedRemote, cancellationToken);

        /// <inheritdoc />
        public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                     CancellationToken cancellationToken = default)
            => channel.RequestAsync(payload, options, channel.DirectedRemote, cancellationToken);

        /// <inheritdoc />
        public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
            => channel.ReceiveAsync(options, cancellationToken);

        /// <summary>握手期间不派发事件：订阅被忽略（未认领的数据报在握手结束后才派发）。</summary>
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived
        {
            add
            {
            }

            remove
            {
            }
        }
    }
}
