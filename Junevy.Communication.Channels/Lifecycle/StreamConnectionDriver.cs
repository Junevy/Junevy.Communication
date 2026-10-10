using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 字节流连接的驱动（计划 9.3）。每次打开都创建新的 <see cref="PendingRequestTable"/>、<see cref="FrameRouter"/> 与 <see cref="StreamChannel"/>，
/// 握手积压（D8）、初始化器与心跳监视器都绑定到这一条连接。故障经 <see cref="ConnectionAttempt"/> 转交给监督器，
/// 且连接结束或打开失败后该尝试被分离，之后的故障报告不会再到达监督器。
/// </summary>
internal sealed class StreamConnectionDriver : IConnectionDriver
{
    private readonly StreamClientOptions options;
    private readonly Func<CancellationToken, Task<CommResult<Stream>>> openStream;
    private readonly Func<Stream, CancellationToken, Task<CommResult<Stream>>> secureStream;
    private readonly Action abortTransport;
    private readonly Func<CancellationToken, Task> closing;
    private readonly Func<PartialFrameAction> partialFrameAction;
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
    public StreamConnectionDriver(StreamClientOptions options, Func<CancellationToken, Task<CommResult<Stream>>> openStream,
                                  Func<Stream, CancellationToken, Task<CommResult<Stream>>> secureStream, Action abortTransport,
                                  Func<CancellationToken, Task> closing, Func<PartialFrameAction> partialFrameAction,
                                  Func<FrameReceivedEventArgs, Task> raise, Func<string> describeEndpoint,
                                  ConnectionStatistics statistics, ILogger logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
        this.secureStream = secureStream ?? throw new ArgumentNullException(nameof(secureStream));
        this.abortTransport = abortTransport ?? throw new ArgumentNullException(nameof(abortTransport));
        this.closing = closing ?? throw new ArgumentNullException(nameof(closing));
        this.partialFrameAction = partialFrameAction ?? throw new ArgumentNullException(nameof(partialFrameAction));
        this.raise = raise ?? throw new ArgumentNullException(nameof(raise));
        this.describeEndpoint = describeEndpoint ?? throw new ArgumentNullException(nameof(describeEndpoint));
        this.statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>当前已建立连接的字节通道；没有连接时为 null。</summary>
    public StreamChannel? CurrentChannel => current?.Channel;

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

        CommResult<Stream> opened = await openStream(cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
            return opened.ToResult();

        // 握手时限从传输打开之后开始计时：流包装（TLS）与初始化器共享这一个窗口（设计文档第 10 节）。握手成功后立即释放窗口，之后不再中止连接。
        using TimeoutScope window = TimeoutScope.Start(options.HandshakeTimeout, cancellationToken, abortTransport);
        LiveConnection? live = null;
        try
        {
            CommResult<Stream> secured = await SecureWithinWindowAsync(opened.Data!, window, cancellationToken).ConfigureAwait(false);
            if (!secured.IsSuccess)
            {
                SafeAbortTransport();
                return secured.ToResult();
            }

            live = CreateConnection(secured.Data!, new ConnectionAttempt(owner, generation));
            live.Channel.Start();

            CommResult handshake = await HandshakeAsync(live, window, cancellationToken).ConfigureAwait(false);
            if (!handshake.IsSuccess)
            {
                await StopConnectionAsync(live, 0, graceful: false).ConfigureAwait(false);
                return handshake;
            }

            window.Dispose();
            StartHeartbeat(live);
            current = live;
            logger.LogInformation("Stream connection opened to {Endpoint}.", describeEndpoint());
            return CommResult.Success();
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

    // 创建一条连接的全部内部对象。关联表先进入握手状态（D8），之后才由调用方启动解析循环，保证连上即发的帧进入积压。
    private LiveConnection CreateConnection(Stream stream, ConnectionAttempt attempt)
    {
        var table = new PendingRequestTable(options.Correlation, options.KeyExtractor, options.LateReplyWindow, logger);
        table.BeginHandshake();

        var router = new FrameRouter(table, options.QueueCapacity, options.QueueFullMode, raise, statistics, logger);
        var channel = new StreamChannel(stream, options.CreateStreamSettings(partialFrameAction()), options.Codec.CreateDecoder(),
                                        options.Codec.CreateEncoder(), table, router, statistics, logger, attempt.Report, abortTransport);
        return new LiveConnection(channel, router, attempt);
    }

    // 在握手时限内执行流包装（TLS）。时限耗尽或用户取消后不再等待包装（它可能忽略取消令牌），但观察它的结果，并释放之后才成功的流。
    private async Task<CommResult<Stream>> SecureWithinWindowAsync(Stream stream, TimeoutScope window, CancellationToken cancellationToken)
    {
        Task<CommResult<Stream>> work = StartSecure(stream, window.Token);
        if (!await WaitForStepAsync(work, window.Token).ConfigureAwait(false))
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

    // 握手：执行初始化器（与 TLS 共享同一握手时限），结束握手并把积压转入派发队列，最后确认握手期间没有发生故障。
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
    private async Task<CommResult> RunInitializerAsync(StreamChannel channel, TimeoutScope window, CancellationToken cancellationToken)
    {
        Task<CommResult> work = StartInitializer(new HandshakeView(channel), window.Token);
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

    private CommResult<T> TimeoutFailure<T>()
        => CommResult<T>.Fail($"The connection handshake did not complete within {options.HandshakeTimeout} ms.", CommErrorKind.Timeout);

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

    // 被放弃的流包装若之后成功，其流不会被使用：观察异常并释放该流（SslStream 不拥有底层流，释放它不影响套接字）。
    private static void ObserveAbandonedStream(Task<CommResult<Stream>> work)
    {
        work.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result.IsSuccess)
                task.Result.Data!.Dispose();
            else if (task.IsFaulted)
                _ = task.Exception;
        }, TaskContinuationOptions.ExecuteSynchronously);
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

        ObserveAbandoned(closingTask);
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

    /// <summary>
    /// 一次打开尝试的故障出口。代次在打开时确定；分离之后不再向监督器转发任何报告。
    /// </summary>
    private sealed class ConnectionAttempt
    {
        private readonly object sync = new object();
        private readonly ConnectionSupervisor owner;
        private readonly long generation;
        private bool detached;
        private bool faulted;

        public ConnectionAttempt(ConnectionSupervisor owner, long generation)
        {
            this.owner = owner;
            this.generation = generation;
        }

        /// <summary>本次尝试是否已经报告过故障。</summary>
        public bool Faulted
        {
            get
            {
                lock (sync)
                {
                    return faulted;
                }
            }
        }

        /// <summary>报告故障（可从任意线程调用）。已分离时忽略。</summary>
        /// <param name="reason">断开原因。</param>
        /// <param name="exception">相关异常，可为 null。</param>
        public void Report(DisconnectReason reason, Exception? exception)
        {
            lock (sync)
            {
                if (detached)
                    return;

                faulted = true;
                owner.ReportConnectionLost(generation, reason, exception);
            }
        }

        /// <summary>分离：之后的报告全部忽略。连接关闭或打开失败时调用。</summary>
        public void Detach()
        {
            lock (sync)
            {
                detached = true;
            }
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

    /// <summary>
    /// 握手期间交给初始化器的字节通道视图：绑定到这一条新连接，不检查 State（此时尚未 Connected）。
    /// 握手期间未认领的帧进入积压，因此视图不派发 <c>FrameReceived</c>（D8）。
    /// </summary>
    private sealed class HandshakeView : IByteChannel
    {
        private readonly StreamChannel channel;

        public HandshakeView(StreamChannel channel)
        {
            this.channel = channel;
        }

        /// <inheritdoc />
        public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => channel.SendAsync(payload, cancellationToken);

        /// <inheritdoc />
        public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                     CancellationToken cancellationToken = default)
            => channel.RequestAsync(payload, options, null, cancellationToken);

        /// <inheritdoc />
        public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
            => channel.ReceiveAsync(options, cancellationToken);

        /// <summary>握手期间不派发事件：订阅被忽略（未认领的帧在握手结束后才派发）。</summary>
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
