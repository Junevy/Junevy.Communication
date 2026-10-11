using Junevy.Communication.Channels.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 服务端事件的派发器（P25 Task 13）：事件进入单个 FIFO 队列，由一个派发循环在锁外逐个触发，保证事件顺序；
/// 派发上下文标记随 AsyncLocal 流入处理器派生的续延，使处理器内的停止或关闭只发出信号、不等待（计划 D16）。
/// </summary>
internal sealed class ServerEventDispatcher
{
    private readonly object sync;
    private readonly ILogger logger;
    private readonly string name;
    private readonly Queue<QueuedEvent> eventQueue = new Queue<QueuedEvent>();
    private readonly AsyncLocal<DispatchScope?> dispatchScope = new AsyncLocal<DispatchScope?>();
    private bool dispatching;

    /// <summary>创建派发器。</summary>
    /// <param name="sync">服务端锁（入队与派发循环都在它下面访问队列）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="name">服务端名称（用于派发失败的日志）。</param>
    public ServerEventDispatcher(object sync, ILogger logger, string name)
    {
        this.sync = sync;
        this.logger = logger;
        this.name = name;
    }

    /// <summary>
    /// 调用方持有服务端锁。事件进入单个派发循环的 FIFO 队列；派发循环在锁外逐个触发。
    /// </summary>
    /// <param name="raise">触发事件的动作。</param>
    /// <param name="delivered">执行之后完成的信号（用于等待 SessionConnected 的派发）；可为 null。</param>
    public void EnqueueLocked(Action raise, TaskCompletionSource<bool>? delivered = null)
    {
        eventQueue.Enqueue(new QueuedEvent(raise, delivered));
        if (dispatching)
            return;

        dispatching = true;
        LifecycleSupport.StartDetached(DispatchEvents);
    }

    /// <summary>
    /// 在派发上下文内执行处理器：标记随 AsyncLocal 流入处理器派生的续延，处理器返回后失效（计划 D16 的派发上下文做法）。
    /// </summary>
    /// <param name="raise">要执行的处理器。</param>
    public void RaiseInDispatch(Action raise)
    {
        var scope = new DispatchScope();
        dispatchScope.Value = scope;
        try
        {
            raise();
        }
        finally
        {
            scope.Exit();
        }
    }

    /// <summary>当前执行流是否位于本服务端某个事件处理器的执行期间。</summary>
    /// <returns>位于派发上下文内时为 true。</returns>
    public bool IsInDispatchContext()
    {
        DispatchScope? scope = dispatchScope.Value;
        return scope != null && scope.IsActive;
    }

    private Task DispatchEvents()
    {
        while (true)
        {
            QueuedEvent item;
            lock (sync)
            {
                if (eventQueue.Count == 0)
                {
                    dispatching = false;
                    return Task.CompletedTask;
                }

                item = eventQueue.Dequeue();
            }

            try
            {
                RaiseInDispatch(item.Raise);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatching a server event of TCP server {Name} failed.", name);
            }
            finally
            {
                item.Delivered?.TrySetResult(true);
            }
        }
    }

    // 一次派发的上下文标记。随 AsyncLocal 流入处理器派生的续延；处理器返回后失效。
    private sealed class DispatchScope
    {
        private int active = 1;

        public bool IsActive => Volatile.Read(ref active) != 0;

        public void Exit() => Volatile.Write(ref active, 0);
    }

    // 派发队列中的一个事件：raise 在派发上下文中执行；delivered 在执行之后完成（用于等待 SessionConnected 的派发）。
    private sealed class QueuedEvent
    {
        public QueuedEvent(Action raise, TaskCompletionSource<bool>? delivered)
        {
            Raise = raise;
            Delivered = delivered;
        }

        public Action Raise { get; }

        public TaskCompletionSource<bool>? Delivered { get; }
    }
}
