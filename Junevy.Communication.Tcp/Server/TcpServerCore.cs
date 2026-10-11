using System.Net;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 服务端的共享状态（P25 Task 13）：服务端锁、状态机、监听器、会话表与计数、停止标志，以及事件派发器。
/// 除 <see cref="State"/> 外，成员只能在持有 <see cref="Sync"/> 时读写；<see cref="State"/> 声明为 volatile 以便无锁读取。
/// 各组件（监听器循环、接入、握手、会话生命周期）经此对象协作，状态变化与事件入队仍在同一临界区内完成。
/// </summary>
internal sealed class TcpServerCore
{
    private readonly Action<ServerStateChangedEventArgs> raiseStateChanged;
    private volatile ServerState state = ServerState.Stopped;

    /// <summary>创建共享状态。</summary>
    /// <param name="name">服务端名称（用于派发失败的日志）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="raiseStateChanged">触发 <c>StateChanged</c> 的回调（发送者为服务端）。</param>
    public TcpServerCore(string name, ILogger logger, Action<ServerStateChangedEventArgs> raiseStateChanged)
    {
        Sync = new object();
        Dispatcher = new ServerEventDispatcher(Sync, logger, name);
        this.raiseStateChanged = raiseStateChanged;
    }

    /// <summary>服务端锁。</summary>
    public object Sync { get; }

    /// <summary>事件派发器（单个派发循环）。</summary>
    public ServerEventDispatcher Dispatcher { get; }

    /// <summary>当前状态（无锁读取）。</summary>
    public ServerState State => state;

    /// <summary>当前监听器；没有监听时为 null。调用方持有 <see cref="Sync"/>。</summary>
    public ITcpListener? Listener { get; set; }

    /// <summary>已绑定的本地端点。调用方持有 <see cref="Sync"/>。</summary>
    public IPEndPoint? LocalEndPoint { get; set; }

    /// <summary>接受循环任务。调用方持有 <see cref="Sync"/>。</summary>
    public Task? AcceptLoop { get; set; }

    /// <summary>重新监听的取消源。调用方持有 <see cref="Sync"/>。</summary>
    public CancellationTokenSource? RestartSource { get; set; }

    /// <summary>停止任务（第一次停止时创建）。调用方持有 <see cref="Sync"/>。</summary>
    public Task? StopTask { get; set; }

    /// <summary>是否已请求停止。调用方持有 <see cref="Sync"/>。</summary>
    public bool StopRequested { get; set; }

    /// <summary>已占用名额的会话数（含握手中的会话）。调用方持有 <see cref="Sync"/>。</summary>
    public int ActiveSessions { get; set; }

    /// <summary>最近分配的会话编号。调用方持有 <see cref="Sync"/>。</summary>
    public long LastSessionId { get; set; }

    /// <summary>已加入会话表的会话（按编号）。调用方持有 <see cref="Sync"/>。</summary>
    public Dictionary<long, TcpSession> Sessions { get; } = new Dictionary<long, TcpSession>();

    /// <summary>仍处于生命周期内的会话（包括握手中的会话）。调用方持有 <see cref="Sync"/>。</summary>
    public HashSet<TcpSession> Live { get; } = new HashSet<TcpSession>();

    /// <summary>
    /// 调用方持有 <see cref="Sync"/>。状态变化与事件入队在同一临界区，保证事件顺序与状态变化顺序一致。
    /// </summary>
    /// <param name="next">新状态。</param>
    /// <param name="exception">导致状态变化的异常；可为 null。</param>
    public void SetStateLocked(ServerState next, Exception? exception)
    {
        ServerState previous = state;
        if (previous == next)
            return;

        state = next;
        var args = new ServerStateChangedEventArgs(previous, next, exception);
        Dispatcher.EnqueueLocked(() => raiseStateChanged(args));
    }

    /// <summary>
    /// 等待任务，或等待取消令牌（取消只停止等待，任务仍会继续）。
    /// </summary>
    /// <param name="task">要等待的任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务完成的任务，或在取消时抛出 <see cref="OperationCanceledException"/>。</returns>
    public static async Task WaitUnlessCancelledAsync(Task task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            Task finished = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
            if (!ReferenceEquals(finished, task))
                cancellationToken.ThrowIfCancellationRequested();
        }

        await task.ConfigureAwait(false);
    }
}
