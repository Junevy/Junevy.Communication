using System.Net;
using System.Threading.Channels;
using Junevy.Communication.Channels.Framing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 帧路由（设计文档 6.2、6.4 节，计划 7.2 节）：入站帧先由 <see cref="PendingRequestTable"/> 认领；
/// 未认领且非迟到应答的帧进入握手积压（握手期间）或有界派发队列；派发循环在单独的任务中串行调用 <c>raise</c>。
/// 认领在调用 <see cref="RouteAsync"/> 的线程上同步完成，不经过派发队列，因此慢的事件处理器不会拖慢应答（6.4 节）。
/// </summary>
/// <remarks>
/// 路由门（<c>routeGate</c>）串行化 <see cref="RouteAsync"/> 与 <see cref="EndHandshakeAsync"/>，保证握手积压先于之后的帧进入派发队列。
/// 解析循环是 <see cref="RouteAsync"/> 的唯一调用方，门在正常情况下不会竞争。
/// </remarks>
internal sealed class FrameRouter : IAsyncDisposable
{
    /// <summary>每累计这么多次队列丢弃记录一次 Warning（计划 7.2 规则 8）。</summary>
    public const int DropWarningInterval = 100;

    private readonly PendingRequestTable table;
    private readonly Func<FrameReceivedEventArgs, Task> raise;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private readonly Channel<QueueItem> queue;
    private readonly SemaphoreSlim routeGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource stopSource = new CancellationTokenSource();

    private long droppedFromQueue;
    private int started;
    private int stopped;
    private Task? dispatchTask;

    /// <summary>
    /// 创建帧路由。
    /// </summary>
    /// <param name="table">关联表。</param>
    /// <param name="queueCapacity">派发队列容量，必须为正。</param>
    /// <param name="fullMode">队列满时的处理方式。</param>
    /// <param name="raise">派发一帧的回调（由调用方提供，例如触发 <c>FrameReceived</c>）；FrameRouter 只保证串行调用与异常隔离。</param>
    /// <param name="statistics">连接统计（记录 FramesDropped）。</param>
    /// <param name="logger">日志记录器。</param>
    public FrameRouter(PendingRequestTable table, int queueCapacity, QueueFullMode fullMode,
                       Func<FrameReceivedEventArgs, Task> raise, ConnectionStatistics statistics, ILogger logger)
    {
        if (table == null)
            throw new ArgumentNullException(nameof(table));
        if (raise == null)
            throw new ArgumentNullException(nameof(raise));
        if (statistics == null)
            throw new ArgumentNullException(nameof(statistics));
        if (logger == null)
            throw new ArgumentNullException(nameof(logger));
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "The queue capacity must be positive.");
        if (!Enum.IsDefined(typeof(QueueFullMode), fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Unknown queue full mode.");

        this.table = table;
        this.raise = raise;
        this.statistics = statistics;
        this.logger = logger;

        var options = new BoundedChannelOptions(queueCapacity)
        {
            FullMode = MapFullMode(fullMode),
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        };

        queue = Channel.CreateBounded<QueueItem>(options, OnItemDropped);
    }

    /// <summary>启动派发循环。只能调用一次。</summary>
    /// <exception cref="InvalidOperationException">已经启动。</exception>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The frame router has already been started.");

        dispatchTask = Task.Run(RunDispatchAsync);
    }

    /// <summary>
    /// 路由一帧：先认领；否则迟到应答丢弃；否则握手积压或派发队列。
    /// Wait 模式下队列满时挂起直到有空位（背压）。
    /// </summary>
    /// <param name="frame">入站帧（调用方已复制为独立数组）。</param>
    /// <param name="remote">来源地址；无来源概念的传输为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="FrameDecodeException">握手积压已满（D8，第 65 帧）。</exception>
    /// <exception cref="ObjectDisposedException">路由已停止。</exception>
    public async ValueTask RouteAsync(byte[] frame, EndPoint? remote, CancellationToken cancellationToken)
    {
        if (frame == null)
            throw new ArgumentNullException(nameof(frame));
        ThrowIfStopped();

        await routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (table.TryComplete(frame, remote))
                return;

            if (table.IsLateReply(frame))
            {
                statistics.IncrementFramesDropped();
                return;
            }

            switch (table.ClassifyUnclaimed(frame, remote))
            {
                case UnclaimedFrameTarget.Backlog:
                    return;

                case UnclaimedFrameTarget.BacklogFull:
                    throw new FrameDecodeException(
                        $"The handshake backlog is full ({PendingRequestTable.MaxHandshakeBacklog} frames); the peer sent too many frames before the handshake completed.");

                default:
                    await EnqueueAsync(new QueueItem(frame, DateTimeOffset.UtcNow, remote), cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        finally
        {
            routeGate.Release();
        }
    }

    /// <summary>
    /// 结束握手：把表中剩余的积压按顺序转入派发队列。与 <see cref="RouteAsync"/> 通过路由门串行化。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    internal async Task EndHandshakeAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopped();

        await routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<(byte[] Frame, EndPoint? Remote)> remaining = table.EndHandshake();
            foreach ((byte[] frame, EndPoint? remote) in remaining)
                await EnqueueAsync(new QueueItem(frame, DateTimeOffset.UtcNow, remote), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            routeGate.Release();
        }
    }

    /// <summary>
    /// 停止派发：取消派发循环（正在执行的 <c>raise</c> 完成后退出），并等待循环结束。队列中尚未派发的帧被丢弃。可重复调用。
    /// </summary>
    public async ValueTask StopAsync()
    {
        Interlocked.Exchange(ref stopped, 1);
        queue.Writer.TryComplete();
        stopSource.Cancel();

        Task? task = dispatchTask;
        if (task != null)
            await task.ConfigureAwait(false);
    }

    /// <summary>等同于 <see cref="StopAsync"/>。</summary>
    public ValueTask DisposeAsync() => StopAsync();

    /// <summary>
    /// 把一个事件派发给每个订阅者：单个订阅者抛出的异常只记 Error 日志，其余订阅者照常收到。
    /// 供 <c>raise</c> 回调使用（计划 7.2 规则 7）。
    /// </summary>
    /// <param name="handler">事件的多播委托；为 null 时无操作。</param>
    /// <param name="sender">事件发送者。</param>
    /// <param name="args">事件参数。</param>
    /// <param name="logger">用于记录订阅者异常的日志记录器。</param>
    internal static void RaiseToSubscribers(EventHandler<FrameReceivedEventArgs>? handler, object sender, FrameReceivedEventArgs args, ILogger logger)
    {
        if (handler == null)
            return;

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<FrameReceivedEventArgs>)subscriber)(sender, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A FrameReceived subscriber threw an exception; the remaining subscribers still receive the frame.");
            }
        }
    }

    private async Task RunDispatchAsync()
    {
        CancellationToken stopToken = stopSource.Token;
        ChannelReader<QueueItem> reader = queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stopToken).ConfigureAwait(false))
            {
                while (!stopToken.IsCancellationRequested && reader.TryRead(out QueueItem item))
                    await DispatchAsync(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // 正常停止。
        }
    }

    private async Task DispatchAsync(QueueItem item)
    {
        var args = new FrameReceivedEventArgs(item.Data, item.ReceivedAt, item.Remote);
        try
        {
            await raise(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dispatching a received frame failed; the router continues with the next frame.");
        }
    }

    private async ValueTask EnqueueAsync(QueueItem item, CancellationToken cancellationToken)
    {
        await queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
    }

    private void OnItemDropped(QueueItem item)
    {
        statistics.IncrementFramesDropped();
        long count = Interlocked.Increment(ref droppedFromQueue);
        if (count % DropWarningInterval == 0)
        {
            logger.LogWarning("The dispatch queue is full; {Count} received frames have been dropped in total.", count);
        }
    }

    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref stopped) != 0)
            throw new ObjectDisposedException(nameof(FrameRouter));
    }

    private static BoundedChannelFullMode MapFullMode(QueueFullMode fullMode)
    {
        switch (fullMode)
        {
            case QueueFullMode.Wait:
                return BoundedChannelFullMode.Wait;

            case QueueFullMode.DropOldest:
                return BoundedChannelFullMode.DropOldest;

            // 注意：BoundedChannelFullMode.DropNewest 会移除队列中最新的已入队帧，再写入新帧；
            // "丢弃新到的帧" 对应的是 DropWrite（丢弃正在写入的帧）。
            case QueueFullMode.DropNewest:
                return BoundedChannelFullMode.DropWrite;

            default:
                throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Unknown queue full mode.");
        }
    }

    private readonly struct QueueItem
    {
        public QueueItem(byte[] data, DateTimeOffset receivedAt, EndPoint? remote)
        {
            Data = data;
            ReceivedAt = receivedAt;
            Remote = remote;
        }

        public byte[] Data { get; }

        public DateTimeOffset ReceivedAt { get; }

        public EndPoint? Remote { get; }
    }
}
