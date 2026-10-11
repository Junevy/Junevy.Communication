using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 字节流与数据报通道共用的停止与故障状态（P25 Task 10，补审第 1 节）：停止标志与停止序列、连接丢失的报告、
/// 写出失败的报告，以及"已关闭"的判定。两种通道的差异只有通道类型名与循环的描述（用于消息），以及字节流在停止时解除管道读写者的阻塞
/// （<c>unblockReaders</c>）。消息文本与拆分前逐字相同。
/// </summary>
internal sealed class ChannelStopSequence
{
    private readonly PendingRequestTable table;
    private readonly FrameRouter router;
    private readonly ILogger logger;
    private readonly Action<DisconnectReason, Exception?> onFault;
    private readonly Action abortTransport;
    private readonly Action? unblockReaders;
    private readonly string closedMessage;
    private readonly string stoppedMessage;
    private readonly string lostConnectionTemplate;
    private readonly string callbackFailedMessage;
    private readonly string loopsTemplate;
    private readonly CancellationTokenSource stopSource = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> stopCompletion =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private int stopRequested;
    private int faultReported;
    private int sendFailureHandled;
    private volatile Task[] loops = Array.Empty<Task>();

    /// <summary>
    /// 创建停止与故障状态。<paramref name="table"/> 与 <paramref name="router"/> 必须已由调用方校验为非 null。
    /// </summary>
    /// <param name="table">关联表（失败在途等待者）。</param>
    /// <param name="router">帧路由（本类在停止时停止它）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="onFault">连接丢失时的回调。</param>
    /// <param name="abortTransport">中止底层传输；必须可以重复调用。</param>
    /// <param name="kind">通道的种类名（<c>stream</c> 或 <c>datagram</c>），用于消息。</param>
    /// <param name="loopsKind">循环的描述（<c>stream loops</c> 或 <c>receive loop</c>），用于超时日志。</param>
    /// <param name="unblockReaders">停止时在取消之后、等待循环之前执行的解除阻塞；不需要时为 null。</param>
    /// <exception cref="ArgumentNullException">日志记录器、回调或中止委托为 null。</exception>
    public ChannelStopSequence(PendingRequestTable table, FrameRouter router, ILogger logger, Action<DisconnectReason, Exception?> onFault,
                               Action abortTransport, string kind, string loopsKind, Action? unblockReaders)
    {
        if (logger == null)
            throw new ArgumentNullException(nameof(logger));
        if (onFault == null)
            throw new ArgumentNullException(nameof(onFault));
        if (abortTransport == null)
            throw new ArgumentNullException(nameof(abortTransport));

        this.table = table;
        this.router = router;
        this.logger = logger;
        this.onFault = onFault;
        this.abortTransport = abortTransport;
        this.unblockReaders = unblockReaders;
        closedMessage = $"The {kind} channel is closed.";
        stoppedMessage = $"The {kind} channel was stopped before the operation completed.";
        lostConnectionTemplate = $"The {kind} channel lost its connection: {{Reason}}.";
        callbackFailedMessage = $"The fault callback of the {kind} channel threw an exception.";
        loopsTemplate = $"The {loopsKind} did not exit within {{Timeout}} ms after the stop request.";
    }

    /// <summary>是否已请求停止。</summary>
    public bool IsStopping => Volatile.Read(ref stopRequested) != 0;

    /// <summary>是否已停止或已报告故障；之后的请求以 <c>ConnectionClosed</c> 失败。</summary>
    public bool IsClosed => IsStopping || Volatile.Read(ref faultReported) != 0;

    /// <summary>停止令牌：停止时取消，用于提前结束等待（例如迟到窗口与填充循环）。</summary>
    public CancellationToken StopToken => stopSource.Token;

    /// <summary>已关闭的失败结果（不带值）。</summary>
    public CommResult ClosedResult() => CommResult.Fail(closedMessage, CommErrorKind.ConnectionClosed);

    /// <summary>已关闭的失败结果（带值类型）。</summary>
    public CommResult<T> ClosedResult<T>() => CommResult<T>.Fail(closedMessage, CommErrorKind.ConnectionClosed);

    /// <summary>登记一个已启动的循环任务；停止时等待它退出。只在启动时调用。</summary>
    /// <param name="task">循环任务（登记后立即可被停止等待）。</param>
    public void AddLoop(Task task)
    {
        Task[] next = new Task[loops.Length + 1];
        loops.CopyTo(next, 0);
        next[loops.Length] = task;
        loops = next;
    }

    /// <summary>
    /// 停止通道（计划 8.2 停止流程）。顺序固定，防止与派发上下文互相等待（审阅者确定）：
    /// 设置停止标志 → 中止传输 → 取消 → 解除阻塞 → 启动路由停止 → 在 <paramref name="drainTimeout"/> 内等待循环退出
    /// → 以 <c>ConnectionClosed</c> 失败所有在途等待者 → 等待路由停止完成。第二次及之后的调用等待同一次停止完成；停止不调用 <c>onFault</c>。
    /// </summary>
    /// <param name="drainTimeout">路由排空已收到帧的最长毫秒数；≤ 0 表示不排空。</param>
    /// <returns>停止完成的任务。</returns>
    public Task Stop(int drainTimeout)
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0)
            _ = CompleteStopAsync(drainTimeout);

        return stopCompletion.Task;
    }

    /// <summary>
    /// 连接丢失（计划 8.2）：同一实例至多报告一次。先以 <c>ConnectionClosed</c> 失败在途等待者，再调用 <c>onFault</c>。
    /// 停止过程中的任何故障都不报告（停止由所有者发起）。
    /// </summary>
    /// <param name="reason">断开原因。</param>
    /// <param name="exception">导致断开的异常；可为 null。</param>
    public void Fault(DisconnectReason reason, Exception? exception)
    {
        if (IsStopping || Interlocked.Exchange(ref faultReported, 1) != 0)
            return;

        logger.LogWarning(exception, lostConnectionTemplate, reason);
        table.FailAll(CommResult.Fail("The connection was lost.", CommErrorKind.ConnectionClosed, null, exception));
        try
        {
            onFault(reason, exception);
        }
        catch (Exception callbackFailure)
        {
            logger.LogError(callbackFailure, callbackFailedMessage);
        }
    }

    /// <summary>
    /// 写出失败（超时、取消或 I/O 错误）：每次写出至多处理一次。先报告 <c>SendFailed</c>，再中止传输：
    /// 中止会让读取端抛出，若先中止，读取端会抢先以 <c>RemoteClosed</c> 报告，掩盖真正的原因。
    /// 停止过程中由停止流程负责中止，这里不再重复中止（停止不报告故障）。
    /// </summary>
    /// <param name="cause">写出异常；超时或取消时为 null。</param>
    public void ReportSendFailure(Exception? cause)
    {
        if (IsStopping || Interlocked.Exchange(ref sendFailureHandled, 1) != 0)
            return;

        Fault(DisconnectReason.SendFailed, cause);
        SafeAbortTransport();
    }

    /// <summary>每次写出开始时调用：写出在发送锁下串行进行，因此这里的标记只属于本次写出。</summary>
    public void BeginWrite() => Volatile.Write(ref sendFailureHandled, 0);

    private async Task CompleteStopAsync(int drainTimeout)
    {
        try
        {
            await PerformStopAsync(drainTimeout).ConfigureAwait(false);
            stopCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            stopCompletion.TrySetException(ex);
        }
    }

    // 停止顺序见 Stop 的文档。路由停止必须在等待循环之前启动：循环可能挂起在满队列上，
    // 而停止可能从 FrameReceived 处理器内发起，此时只有路由停止（完成派发队列写端）才能让循环退出。
    private async Task PerformStopAsync(int drainTimeout)
    {
        SafeAbortTransport();
        stopSource.Cancel();
        unblockReaders?.Invoke();

        Task routerStop = router.StopAsync(drainTimeout).AsTask();
        await WaitForLoopsAsync(drainTimeout).ConfigureAwait(false);

        table.FailAll(CommResult.Fail(stoppedMessage, CommErrorKind.ConnectionClosed));
        await routerStop.ConfigureAwait(false);
    }

    private async Task WaitForLoopsAsync(int drainTimeout)
    {
        Task[] running = loops;
        if (running.Length == 0)
            return;

        Task all = Task.WhenAll(running);
        Task finished = await Task.WhenAny(all, Task.Delay(Math.Max(drainTimeout, 0))).ConfigureAwait(false);
        if (!ReferenceEquals(finished, all) && drainTimeout > 0)
            logger.LogWarning(loopsTemplate, drainTimeout);
    }

    private void SafeAbortTransport()
    {
        try
        {
            abortTransport();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aborting the transport failed; the stop continues.");
        }
    }
}
