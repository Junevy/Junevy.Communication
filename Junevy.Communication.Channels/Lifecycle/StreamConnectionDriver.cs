using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 字节流连接的驱动（计划 9.3）。每次打开都创建新的 <see cref="PendingRequestTable"/>、<see cref="FrameRouter"/> 与 <see cref="StreamChannel"/>，
/// 握手积压（D8）、初始化器与心跳监视器都绑定到这一条连接。故障经 <see cref="ConnectionAttempt"/> 转交给监督器，
/// 且连接结束或打开失败后该尝试被分离，之后的故障报告不会再到达监督器。握手与探测由 <see cref="HandshakeRunner"/> 与 <see cref="ClientProbeSource"/> 共用。
/// </summary>
internal sealed class StreamConnectionDriver : IConnectionDriver
{
    private readonly StreamClientOptions options;
    private readonly IByteChannel owner;
    private readonly Func<CancellationToken, Task<CommResult<Stream>>> openStream;
    private readonly Func<Stream, CancellationToken, Task<CommResult<Stream>>> secureStream;
    private readonly Action abortTransport;
    private readonly Func<CancellationToken, Task> closing;
    private readonly Func<PartialFrameAction> partialFrameAction;
    private readonly Func<FrameReceivedEventArgs, Task> raise;
    private readonly Func<string> describeEndpoint;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private readonly HandshakeRunner handshakeRunner;
    private readonly ClientProbeSource probeSource;
    private ConnectionSupervisor? supervisor;
    private volatile LiveConnection? current;

    /// <summary>
    /// 创建驱动（未绑定监督器，需随后调用 <see cref="Attach"/>）。
    /// </summary>
    /// <param name="options">已校验的运行参数。</param>
    /// <param name="owner">公开的通道实例；<see cref="ChannelComponents.HealthProbeFactory"/> 以它为参数调用。</param>
    /// <param name="openStream">打开传输（派生类的 OpenStreamAsync）。</param>
    /// <param name="secureStream">握手时限内的流包装（派生类的 SecureStreamAsync）。</param>
    /// <param name="abortTransport">中止传输（派生类的 AbortTransport）；必须可重复调用。</param>
    /// <param name="closing">优雅关闭前的动作（派生类的 OnClosingAsync）。</param>
    /// <param name="partialFrameAction">读取派生类的 PartialFrameAction（每次打开时读取）。</param>
    /// <param name="raise">派发一帧 FrameReceived 的回调。</param>
    /// <param name="describeEndpoint">日志用的端点描述。</param>
    /// <param name="statistics">连接统计（跨代次累计）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public StreamConnectionDriver(StreamClientOptions options, IByteChannel owner, Func<CancellationToken, Task<CommResult<Stream>>> openStream,
                                  Func<Stream, CancellationToken, Task<CommResult<Stream>>> secureStream, Action abortTransport,
                                  Func<CancellationToken, Task> closing, Func<PartialFrameAction> partialFrameAction,
                                  Func<FrameReceivedEventArgs, Task> raise, Func<string> describeEndpoint,
                                  ConnectionStatistics statistics, ILogger logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
        this.secureStream = secureStream ?? throw new ArgumentNullException(nameof(secureStream));
        this.abortTransport = abortTransport ?? throw new ArgumentNullException(nameof(abortTransport));
        this.closing = closing ?? throw new ArgumentNullException(nameof(closing));
        this.partialFrameAction = partialFrameAction ?? throw new ArgumentNullException(nameof(partialFrameAction));
        this.raise = raise ?? throw new ArgumentNullException(nameof(raise));
        this.describeEndpoint = describeEndpoint ?? throw new ArgumentNullException(nameof(describeEndpoint));
        this.statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        handshakeRunner = new HandshakeRunner(options.Core.Initializer, CancellationToken.None, TimeoutResult,
                                              cause => logger.LogWarning(cause, "The connection initializer threw an exception."));
        probeSource = new ClientProbeSource(options.Core, owner, statistics, logger, describeEndpoint);
    }

    /// <summary>当前已建立连接的字节通道；没有连接时为 null。</summary>
    public StreamChannel? CurrentChannel => current?.Channel;

    /// <summary>
    /// 绑定监督器（只能调用一次，在任何打开之前）。
    /// </summary>
    /// <param name="owner">监督器。</param>
    /// <exception cref="InvalidOperationException">已经绑定。</exception>
    public void Attach(ConnectionSupervisor owner) => SupervisorBinding.Attach(ref supervisor, owner);

    /// <inheritdoc />
    public async Task<CommResult> OpenAsync(CancellationToken cancellationToken)
    {
        ConnectionSupervisor owner = supervisor ?? throw new InvalidOperationException("The connection driver has not been attached to a supervisor.");
        long generation = owner.OpeningGeneration;

        CommResult<Stream> opened = await openStream(cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
            return opened.ToResult();

        // 握手时限从传输打开之后开始计时：流包装（TLS）与初始化器共享这一个窗口（设计文档第 10 节）。握手成功后立即释放窗口，之后不再中止连接。
        using TimeoutScope window = TimeoutScope.Start(options.Core.HandshakeTimeout, cancellationToken, abortTransport);
        LiveConnection? live = null;
        try
        {
            CommResult<Stream> secured = await SecureWithinWindowAsync(opened.Data, window, cancellationToken).ConfigureAwait(false);
            if (!secured.IsSuccess)
            {
                SafeAbortTransport();
                return secured.ToResult();
            }

            live = CreateConnection(secured.Data, new ConnectionAttempt(owner, generation));
            live.Channel.Start();
            return await CompleteOpenAsync(live, window, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (live != null)
                await StopConnectionAsync(live, 0, graceful: false).ConfigureAwait(false);
            else
                SafeAbortTransport();

            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Opening the stream connection to {Endpoint} failed.", describeEndpoint());
            if (live != null)
                await StopConnectionAsync(live, 0, graceful: false).ConfigureAwait(false);
            else
                SafeAbortTransport();

            return CommResult.Fail("Opening the stream connection failed.", CommErrorKind.Unspecified, null, ex);
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(DisconnectReason reason, int drainTimeout)
    {
        LiveConnection? live = current;
        current = null;
        if (live == null)
            return;

        logger.LogDebug("Closing the stream connection to {Endpoint} ({Reason}).", describeEndpoint(), reason);
        await StopConnectionAsync(live, drainTimeout, graceful: true).ConfigureAwait(false);
    }

    // 握手、工厂探测与心跳。握手或探测失败时停止连接（本次打开失败）；成功后连接才成为当前连接。
    private async Task<CommResult> CompleteOpenAsync(LiveConnection live, TimeoutScope window, CancellationToken cancellationToken)
    {
        CommResult handshake = await handshakeRunner.CompleteClientHandshakeAsync(new StreamChannelView(live.Channel), live.Router, live.Attempt,
                                                                                  window, cancellationToken).ConfigureAwait(false);
        if (!handshake.IsSuccess)
        {
            await StopConnectionAsync(live, 0, graceful: false).ConfigureAwait(false);
            return handshake;
        }

        window.Dispose();

        // 工厂探测在启动心跳之前解析；失败使本次打开失败，连接随即关闭（与握手失败一致）。
        CommResult probing = probeSource.Resolve();
        if (!probing.IsSuccess)
        {
            await StopConnectionAsync(live, 0, graceful: false).ConfigureAwait(false);
            return probing;
        }

        live.Heartbeat = probeSource.CreateHeartbeat(live.Attempt);
        live.Heartbeat?.Start();
        current = live;
        logger.LogInformation("Stream connection opened to {Endpoint}.", describeEndpoint());
        return CommResult.Success();
    }

    // 创建一条连接的全部内部对象。关联表先进入握手状态（D8），之后才由调用方启动解析循环，保证连上即发的帧进入积压。
    private LiveConnection CreateConnection(Stream stream, ConnectionAttempt attempt)
    {
        var table = new PendingRequestTable(options.Core.Correlation, options.Core.KeyExtractor, options.Core.LateReplyWindow, logger);
        table.BeginHandshake();

        var router = new FrameRouter(table, options.Core.QueueCapacity, options.Core.QueueFullMode, raise, statistics, logger);
        var channel = new StreamChannel(stream, options.CreateStreamSettings(partialFrameAction()), options.Codec.CreateDecoder(),
                                        options.Codec.CreateEncoder(), table, router, statistics, logger, attempt.Report, abortTransport);
        return new LiveConnection(channel, router, attempt);
    }

    // 在握手时限内执行流包装（TLS）。时限耗尽或用户取消后不再等待包装（它可能忽略取消令牌），但观察它的结果，并释放之后才成功的流。
    private async Task<CommResult<Stream>> SecureWithinWindowAsync(Stream stream, TimeoutScope window, CancellationToken cancellationToken)
    {
        Task<CommResult<Stream>> work = StartSecure(stream, window.Token);
        if (!await HandshakeRunner.WaitForStepAsync(work, window.Token, CancellationToken.None).ConfigureAwait(false))
        {
            ObserveAbandonedStream(work);
            cancellationToken.ThrowIfCancellationRequested();
            return TimeoutFailure<Stream>();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ObserveAbandonedStream(work);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // 时限可能在包装失败（例如套接字被中止引发的 I/O 异常）之前耗尽：此时归类为超时，而不是连接关闭。
        if (work.IsCanceled || window.IsTimedOut)
        {
            ObserveAbandonedStream(work);
            return TimeoutFailure<Stream>();
        }

        if (work.IsFaulted)
        {
            Exception? cause = work.Exception?.InnerException ?? work.Exception;
            logger.LogWarning(cause, "Securing the stream failed.");
            return CommResult<Stream>.Fail("Securing the stream failed.", CommErrorKind.Unspecified, null, cause);
        }

        return work.Result;
    }

    private CommResult TimeoutResult()
        => CommResult.Fail($"The connection handshake did not complete within {options.Core.HandshakeTimeout} ms.", CommErrorKind.Timeout);

    private CommResult<T> TimeoutFailure<T>()
        => CommResult<T>.Fail($"The connection handshake did not complete within {options.Core.HandshakeTimeout} ms.", CommErrorKind.Timeout);

    // 同步抛出的异常转换为已完成的故障任务，统一由 SecureWithinWindowAsync 处理。
    private Task<CommResult<Stream>> StartSecure(Stream stream, CancellationToken token)
    {
        try
        {
            return secureStream(stream, token);
        }
        catch (Exception ex)
        {
            return Task.FromException<CommResult<Stream>>(ex);
        }
    }

    // 被放弃的流包装若之后成功，其流不会被使用：观察异常并释放该流（SslStream 不拥有底层流，释放它不影响套接字）。
    private static void ObserveAbandonedStream(Task<CommResult<Stream>> work)
    {
        work.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result.IsSuccess)
                task.Result.Data.Dispose();
            else if (task.IsFaulted)
                _ = task.Exception;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    // 停止一条连接。先分离尝试（之后的故障不再报告），再停止心跳，最后停止字节通道（它会中止传输）。
    // 优雅关闭时先执行派生类的关闭动作，并最多等待 drainTimeout 毫秒。
    private async Task StopConnectionAsync(LiveConnection live, int drainTimeout, bool graceful)
    {
        live.Attempt.Detach();

        if (live.Heartbeat != null)
            await live.Heartbeat.StopAsync().ConfigureAwait(false);

        if (graceful)
            await RunClosingAsync(drainTimeout).ConfigureAwait(false);

        try
        {
            await live.Channel.StopAsync(drainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stopping the stream channel failed; the transport is aborted.");
            SafeAbortTransport();
        }
    }

    private async Task RunClosingAsync(int drainTimeout)
    {
        Task closingTask;
        try
        {
            closingTask = closing(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The graceful close action threw an exception.");
            return;
        }

        if (drainTimeout > 0)
            await Task.WhenAny(closingTask, Task.Delay(drainTimeout)).ConfigureAwait(false);

        HandshakeRunner.ObserveAbandoned(closingTask);
    }

    private void SafeAbortTransport()
    {
        try
        {
            abortTransport();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aborting the transport failed.");
        }
    }

    /// <summary>一条已打开的字节流连接及其内部对象。</summary>
    private sealed class LiveConnection
    {
        public LiveConnection(StreamChannel channel, FrameRouter router, ConnectionAttempt attempt)
        {
            Channel = channel;
            Router = router;
            Attempt = attempt;
        }

        public StreamChannel Channel { get; }

        public FrameRouter Router { get; }

        public ConnectionAttempt Attempt { get; }

        public HeartbeatMonitor? Heartbeat { get; set; }
    }
}
