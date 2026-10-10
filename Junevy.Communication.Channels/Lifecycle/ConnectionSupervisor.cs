using System.Threading.Channels;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 连接生命周期（设计文档 6.1 节，计划 9.2）：串行化 Connect、Disconnect、重连尝试、连接丢失处理与释放；
/// 维护连接代次与状态机；按顺序、在锁外派发状态事件。
/// </summary>
/// <remarks>
/// 锁层次：<c>lifecycleGate</c>（长操作，持有期间可以调用驱动）→ <c>stateSync</c>（短临界区：只改字段、写事件队列，不调用驱动与事件处理器）。
/// <see cref="ReportConnectionLost"/> 可从任意线程（解析循环、计时器）调用：只做检查并调度后台处理，不等待任何锁或停止流程，因此不会与调用方自锁。
/// 状态事件由单个派发任务处理；派发上下文（<c>stateDispatchScope</c>）内的 <see cref="DisposeAsync"/> 不等待该派发任务结束（计划 D16）。
/// </remarks>
internal sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly string name;
    private readonly IConnectionDriver driver;
    private readonly IBackoffPolicy? reconnectPolicy;
    private readonly bool reconnectOnInitialFailure;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;

    private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
    private readonly object stateSync = new object();
    private readonly CancellationTokenSource disposeSource = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> closeCompletion =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ConnectionStateChangedEventArgs> stateEvents = Channel.CreateUnbounded<ConnectionStateChangedEventArgs>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly AsyncLocal<DispatchScope?> stateDispatchScope = new AsyncLocal<DispatchScope?>();
    private readonly List<TaskCompletionSource<bool>> connectedWaiters = new List<TaskCompletionSource<bool>>();

    // 以下字段由 stateSync 保护；state 例外，声明为 volatile 以便无锁读取。
    private volatile ConnectionState state = ConnectionState.Disconnected;
    private long generationCounter;
    private bool openInProgress;
    private (DisconnectReason Reason, Exception? Exception)? pendingLoss;
    private bool userDisconnected;
    private bool disposed;
    private long lossScheduledGeneration = -1;
    private CancellationTokenSource? reconnectSource;
    private Task? reconnectTask;
    private Task? stateDispatchTask;

    /// <summary>
    /// 创建连接监督器（初始状态为 Disconnected，未发起连接）。
    /// </summary>
    /// <param name="name">连接名称（日志用）。</param>
    /// <param name="driver">连接驱动。</param>
    /// <param name="reconnectPolicy">重连退避策略；为 null 表示未启用重连。</param>
    /// <param name="reconnectOnInitialFailure">首次连接失败是否也转入后台重连（仅在启用重连时生效）。</param>
    /// <param name="statistics">连接统计（跨代次累计）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public ConnectionSupervisor(string name, IConnectionDriver driver, IBackoffPolicy? reconnectPolicy, bool reconnectOnInitialFailure,
                                ConnectionStatistics statistics, ILogger logger)
    {
        this.name = name ?? throw new ArgumentNullException(nameof(name));
        this.driver = driver ?? throw new ArgumentNullException(nameof(driver));
        this.statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.reconnectPolicy = reconnectPolicy;
        this.reconnectOnInitialFailure = reconnectOnInitialFailure;
    }

    /// <summary>当前连接状态（无锁读取）。</summary>
    public ConnectionState State => state;

    /// <summary>当前连接的代次：每次成功打开加 1，从 0 开始。</summary>
    public long Generation => Interlocked.Read(ref generationCounter);

    /// <summary>
    /// 正在打开的连接的代次（仅在驱动的 <c>OpenAsync</c> 执行期间有意义，即 <see cref="Generation"/> + 1）。
    /// 驱动用它标记自己的故障报告。
    /// </summary>
    internal long OpeningGeneration => Interlocked.Read(ref generationCounter) + 1;

    /// <summary>状态变化事件；按顺序、在锁外触发，每个订阅者单独隔离异常。</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 发起连接。已连接时直接返回成功；处于重连中时先取消并等待重连循环退出。
    /// 用户取消抛出 <see cref="OperationCanceledException"/>（状态回到 Disconnected）。
    /// </summary>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>连接结果。</returns>
    /// <exception cref="ObjectDisposedException">已释放。</exception>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (state == ConnectionState.Connected)
                return CommResult.Success();

            await CancelAndAwaitReconnectLoopAsync().ConfigureAwait(false);

            lock (stateSync)
            {
                userDisconnected = false;
                SetStateLocked(ConnectionState.Connecting, DisconnectReason.None, null, 0);
            }

            CommResult result;
            try
            {
                result = await OpenOnceAsync(cancellationToken, 0).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(ConnectionState.Disconnected, DisconnectReason.None, null, 0);
                throw;
            }

            if (!result.IsSuccess)
                EnterStateAfterFailedInitialConnect(result);

            return result;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 用户主动断开：停止后台重连，此后不会自动连回。已断开或已释放时直接返回。
    /// </summary>
    /// <param name="drainTimeout">优雅关闭的等待时间（毫秒）。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>断开完成的任务。</returns>
    public async Task DisconnectAsync(int drainTimeout, CancellationToken cancellationToken)
    {
        lock (stateSync)
        {
            if (disposed)
                return;

            // 标记在临界区内设置：重连循环的创建也在 stateSync 下检查标记，因此之后不会再创建新的循环。
            userDisconnected = true;
        }

        // 先停止重连循环，避免它在等待生命周期锁期间继续打开连接。
        await CancelAndAwaitReconnectLoopAsync().ConfigureAwait(false);

        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 等待锁期间被取消：循环已停止，状态不能停留在没有循环的 Reconnecting 上。
            await CancelAndAwaitReconnectLoopAsync().ConfigureAwait(false);
            lock (stateSync)
            {
                if (state == ConnectionState.Reconnecting)
                    SetStateLocked(ConnectionState.Disconnected, DisconnectReason.UserRequested, null, 0);
            }

            throw;
        }

        try
        {
            // 持有生命周期锁时再次停止：等待锁期间 ConnectAsync 可能清除了标记，并在打开失败后启动了新的循环。
            await CancelAndAwaitReconnectLoopAsync().ConfigureAwait(false);

            ConnectionState current = state;
            if (current == ConnectionState.Connected)
            {
                SetState(ConnectionState.Disconnecting, DisconnectReason.UserRequested, null, 0);
                await CloseDriverAsync(DisconnectReason.UserRequested, drainTimeout).ConfigureAwait(false);
                SetState(ConnectionState.Disconnected, DisconnectReason.UserRequested, null, 0);
            }
            else if (current == ConnectionState.Reconnecting)
            {
                SetState(ConnectionState.Disconnected, DisconnectReason.UserRequested, null, 0);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 等待进入 Connected。已连接时立即返回 true。
    /// </summary>
    /// <param name="timeout">超时（毫秒）；0 表示只检查当前状态；-1 表示无限等待。</param>
    /// <param name="cancellationToken">用户取消令牌（取消抛出 <see cref="OperationCanceledException"/>）。</param>
    /// <returns>进入 Connected 返回 true；超时或释放返回 false。</returns>
    /// <exception cref="ArgumentOutOfRangeException">超时小于 -1。</exception>
    public async Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (timeout < System.Threading.Timeout.Infinite)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be -1 (infinite) or a non-negative number of milliseconds.");

        TaskCompletionSource<bool> waiter;
        lock (stateSync)
        {
            if (state == ConnectionState.Connected)
                return true;

            if (disposed || timeout == 0)
                return false;

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connectedWaiters.Add(waiter);
        }

        try
        {
            Task delay = Task.Delay(timeout, cancellationToken);
            Task finished = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
            if (ReferenceEquals(finished, waiter.Task))
                return await waiter.Task.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            lock (stateSync)
            {
                connectedWaiters.Remove(waiter);
            }
        }
    }

    /// <summary>
    /// 报告连接丢失（可从任意线程调用，不阻塞）。代次不等于当前连接，或状态不是 Connected，则忽略；
    /// 但若报告属于正在打开的连接（驱动仍在执行 <c>OpenAsync</c>），则记住该故障，在连接建立后立即按丢失处理。
    /// 实际处理（关闭、重连）在后台进行，调用方不需要等待。
    /// </summary>
    /// <param name="generation">报告所属连接的代次。</param>
    /// <param name="reason">断开原因。</param>
    /// <param name="exception">相关异常，可为 null。</param>
    public void ReportConnectionLost(long generation, DisconnectReason reason, Exception? exception)
    {
        lock (stateSync)
        {
            if (disposed)
                return;

            long current = generationCounter;
            if (state == ConnectionState.Connected && generation == current)
            {
                // 同一代次只处理一次：重复报告（例如多个故障源同时报告）被忽略。
                if (lossScheduledGeneration != generation)
                    ScheduleLossLocked(generation, reason, exception);

                return;
            }

            if (openInProgress && generation == current + 1 && pendingLoss == null)
                pendingLoss = (reason, exception);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // 派发上下文必须在任何等待之前判定：此时调用方仍位于状态事件处理器之内。
        bool inDispatchContext = IsInStateDispatchContext();
        bool first;
        lock (stateSync)
        {
            first = !disposed;
            disposed = true;
        }

        if (first)
            _ = CloseForDisposeAsync();

        return new ValueTask(WaitForDisposeAsync(inDispatchContext));
    }

    private async Task WaitForDisposeAsync(bool inDispatchContext)
    {
        await closeCompletion.Task.ConfigureAwait(false);

        // 派发上下文内只发出停止信号，不等待状态派发任务：该任务正在执行调用方的处理器，等待会自锁（D16）。
        if (!inDispatchContext)
            await WaitForStateDispatchAsync().ConfigureAwait(false);
    }

    // 释放：取消重连与在途的打开 → 等待重连循环退出 → 获取生命周期锁 → 关闭连接 → 进入 Disposed → 结束状态派发队列。
    private async Task CloseForDisposeAsync()
    {
        try
        {
            disposeSource.Cancel();
            await CancelAndAwaitReconnectLoopAsync().ConfigureAwait(false);

            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await CloseDriverAsync(DisconnectReason.Disposed, 0).ConfigureAwait(false);
                SetState(ConnectionState.Disposed, DisconnectReason.Disposed, null, 0);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Disposing the connection {Name} failed.", name);
        }
        finally
        {
            lock (stateSync)
            {
                stateEvents.Writer.TryComplete();
            }

            closeCompletion.TrySetResult(true);
        }
    }

    // 打开一次（调用方持有生命周期锁）。成功时完成连接（代次、统计、Connected 事件，并处理打开期间的故障）；失败时只返回结果，由调用方决定状态。
    private async Task<CommResult> OpenOnceAsync(CancellationToken cancellationToken, int reconnectAttempt)
    {
        lock (stateSync)
        {
            openInProgress = true;
            pendingLoss = null;
        }

        CommResult result;
        try
        {
            result = await OpenDriverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EndOpenAttempt();
            throw;
        }

        if (!result.IsSuccess)
        {
            EndOpenAttempt();
            return result;
        }

        // 结束打开标记与完成连接在同一临界区内进行：此前报告的故障由 CompleteConnectionLocked 处理，之后的报告因状态已是 Connected 而直接调度。
        bool closeNow;
        lock (stateSync)
        {
            openInProgress = false;
            closeNow = disposed;
            if (!closeNow)
                CompleteConnectionLocked(reconnectAttempt);
        }

        if (closeNow)
        {
            // 打开成功的同时开始了释放：不建立连接，直接关闭。
            await CloseDriverAsync(DisconnectReason.Disposed, 0).ConfigureAwait(false);
            return CommResult.Fail("The channel was disposed while the connection was being opened.", CommErrorKind.ConnectionClosed);
        }

        return result;
    }

    // 驱动的 OpenAsync：用户取消原样抛出；释放引起的取消与驱动内部取消转换为失败结果；其它异常记录后转换为失败结果。
    private async Task<CommResult> OpenDriverAsync(CancellationToken userToken)
    {
        using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, disposeSource.Token))
        {
            try
            {
                return await driver.OpenAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (userToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                return CommResult.Fail("The connection attempt was aborted.", CommErrorKind.ConnectionClosed, null, ex);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Opening the connection {Name} failed with an unexpected exception.", name);
                return CommResult.Fail("Opening the connection failed with an unexpected exception.", CommErrorKind.Unspecified, null, ex);
            }
        }
    }

    // 结束一次打开尝试：清除打开标记与待处理的故障（失败的尝试不会把故障带到下一次连接）。
    private void EndOpenAttempt()
    {
        lock (stateSync)
        {
            openInProgress = false;
            pendingLoss = null;
        }
    }

    // 连接建立：代次加 1，统计与状态更新，并处理打开期间记住的故障。调用方持有 stateSync 与生命周期锁。
    private void CompleteConnectionLocked(int reconnectAttempt)
    {
        long next = generationCounter + 1;
        Interlocked.Exchange(ref generationCounter, next);
        if (reconnectAttempt > 0)
            statistics.IncrementReconnectCount();

        statistics.SetConnectedSince(DateTimeOffset.UtcNow);
        SetStateLocked(ConnectionState.Connected, DisconnectReason.None, null, reconnectAttempt);

        if (pendingLoss.HasValue)
        {
            (DisconnectReason reason, Exception? exception) = pendingLoss.Value;
            pendingLoss = null;
            ScheduleLossLocked(next, reason, exception);
        }
    }

    // 首次连接失败：启用重连且允许首次失败重连时转入 Reconnecting 并启动循环，否则回到 Disconnected。
    private void EnterStateAfterFailedInitialConnect(CommResult result)
    {
        DisconnectReason reason = result.ErrorKind == CommErrorKind.AuthenticationFailed
            ? DisconnectReason.AuthenticationFailed
            : DisconnectReason.Error;

        lock (stateSync)
        {
            if (!disposed && reconnectPolicy != null && reconnectOnInitialFailure && !userDisconnected)
            {
                SetStateLocked(ConnectionState.Reconnecting, reason, result.Exception, 0);
                StartReconnectLoopLocked();
            }
            else
            {
                SetStateLocked(ConnectionState.Disconnected, reason, result.Exception, 0);
            }
        }
    }

    // 调度连接丢失的后台处理（stateSync 下调用）。同一代次只调度一次。
    private void ScheduleLossLocked(long generation, DisconnectReason reason, Exception? exception)
    {
        lossScheduledGeneration = generation;
        LifecycleSupport.StartDetached(() => HandleConnectionLostAsync(generation, reason, exception));
    }

    // 连接丢失的后台处理：获取生命周期锁 → 再次校验 → 关闭该代次 → 按策略进入 Reconnecting 或 Disconnected。
    private async Task HandleConnectionLostAsync(long generation, DisconnectReason reason, Exception? exception)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool reconnect;
            lock (stateSync)
            {
                if (disposed || state != ConnectionState.Connected || generationCounter != generation)
                    return;

                reconnect = reconnectPolicy != null && !userDisconnected;
            }

            logger.LogWarning(exception, "Connection {Name} was lost: {Reason}.", name, reason);
            await CloseDriverAsync(reason, 0).ConfigureAwait(false);

            lock (stateSync)
            {
                if (disposed)
                    return;

                if (reconnect && !userDisconnected)
                {
                    SetStateLocked(ConnectionState.Reconnecting, reason, exception, 0);
                    StartReconnectLoopLocked();
                }
                else
                {
                    SetStateLocked(ConnectionState.Disconnected, reason, exception, 0);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    // 后台重连循环（独立的取消源）：退避 → 获取生命周期锁 → 打开一次。成功即结束；耗尽则回到 Disconnected（ReconnectExhausted）。
    private async Task RunReconnectLoopAsync(CancellationToken token)
    {
        int attempt = 0;
        try
        {
            while (true)
            {
                attempt++;
                int? delay = reconnectPolicy!.GetDelay(attempt);
                if (delay == null)
                {
                    await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        lock (stateSync)
                        {
                            SetStateLocked(ConnectionState.Disconnected, DisconnectReason.ReconnectExhausted, null, attempt - 1);
                        }
                    }
                    finally
                    {
                        lifecycleGate.Release();
                    }

                    logger.LogWarning("Reconnecting {Name} gave up after {Attempts} attempt(s).", name, attempt - 1);
                    return;
                }

                await Task.Delay(delay.Value, token).ConfigureAwait(false);
                await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    CommResult result = await OpenOnceAsync(token, attempt).ConfigureAwait(false);
                    if (result.IsSuccess)
                        return;

                    logger.LogInformation("Reconnect attempt {Attempt} for {Name} failed: {Result}", attempt, name, result);
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 被用户断开、重新连接或释放取消：状态由取消方负责。
        }
    }

    // 调用方持有 stateSync。启动循环与标记 Reconnecting 在同一临界区，保证 DisconnectAsync 要么看到循环，要么循环看到用户断开标记。
    private void StartReconnectLoopLocked()
    {
        var source = new CancellationTokenSource();
        reconnectSource = source;
        reconnectTask = LifecycleSupport.StartDetached(() => RunReconnectLoopAsync(source.Token));
    }

    // 取消重连循环并返回其任务（不等待）。取消在临界区外进行，避免取消回调在锁内执行。
    private Task? CancelReconnectLoop()
    {
        CancellationTokenSource? source;
        Task? loop;
        lock (stateSync)
        {
            source = reconnectSource;
            loop = reconnectTask;
        }

        source?.Cancel();
        return loop;
    }

    // 取消并等待重连循环退出。循环一旦被取消就不再需要生命周期锁即可退出（锁等待与延时都可取消），
    // 因此调用方无论是否持有生命周期锁，等待都不会自锁。
    private async Task CancelAndAwaitReconnectLoopAsync()
    {
        Task? loop = CancelReconnectLoop();
        if (loop != null)
            await loop.ConfigureAwait(false);
    }

    private async Task CloseDriverAsync(DisconnectReason reason, int drainTimeout)
    {
        try
        {
            await driver.CloseAsync(reason, drainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Closing the connection {Name} failed.", name);
        }
    }

    private void SetState(ConnectionState next, DisconnectReason reason, Exception? exception, int reconnectAttempt)
    {
        lock (stateSync)
        {
            SetStateLocked(next, reason, exception, reconnectAttempt);
        }
    }

    // 调用方持有 stateSync。状态与事件入队在同一临界区，保证事件顺序与状态变化顺序一致。
    private void SetStateLocked(ConnectionState next, DisconnectReason reason, Exception? exception, int reconnectAttempt)
    {
        ConnectionState previous = state;
        if (previous == next)
            return;

        state = next;
        if (previous == ConnectionState.Connected)
            statistics.SetConnectedSince(null);

        if (next == ConnectionState.Connected || next == ConnectionState.Disposed)
        {
            foreach (TaskCompletionSource<bool> waiter in connectedWaiters)
                waiter.TrySetResult(next == ConnectionState.Connected);

            connectedWaiters.Clear();
        }

        if (stateDispatchTask == null)
            stateDispatchTask = LifecycleSupport.StartDetached(RunStateDispatchAsync);

        stateEvents.Writer.TryWrite(new ConnectionStateChangedEventArgs(previous, next, reason, exception, reconnectAttempt));
    }

    // 状态事件派发任务：单个任务按入队顺序逐个触发，不持有任何锁。
    private async Task RunStateDispatchAsync()
    {
        ChannelReader<ConnectionStateChangedEventArgs> reader = stateEvents.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out ConnectionStateChangedEventArgs? args))
                RaiseStateChanged(args!);
        }
    }

    private void RaiseStateChanged(ConnectionStateChangedEventArgs args)
    {
        var scope = new DispatchScope();
        stateDispatchScope.Value = scope;
        try
        {
            LifecycleSupport.InvokeEach(StateChanged, this, args, logger, "StateChanged");
        }
        finally
        {
            scope.Exit();
        }
    }

    // 当前执行流是否位于本监督器某次状态事件处理器的执行期间（标记随 AsyncLocal 流入处理器派生的续延，处理器返回后失效）。
    private bool IsInStateDispatchContext()
    {
        DispatchScope? scope = stateDispatchScope.Value;
        return scope != null && scope.IsActive;
    }

    private async Task WaitForStateDispatchAsync()
    {
        Task? dispatch;
        lock (stateSync)
        {
            dispatch = stateDispatchTask;
        }

        if (dispatch != null)
            await dispatch.ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        bool isDisposed;
        lock (stateSync)
        {
            isDisposed = disposed;
        }

        if (isDisposed)
            throw new ObjectDisposedException(nameof(ConnectionSupervisor));
    }

    // 一次状态事件派发的上下文标记。随 AsyncLocal 流入处理器派生的续延；处理器返回后失效。
    private sealed class DispatchScope
    {
        private int active = 1;

        public bool IsActive => Volatile.Read(ref active) != 0;

        public void Exit() => Volatile.Write(ref active, 0);
    }
}
