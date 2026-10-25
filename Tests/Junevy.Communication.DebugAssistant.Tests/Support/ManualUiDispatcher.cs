using Junevy.Communication.DebugAssistant.Services;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 测试用的界面派发：动作先排队，由测试调用 <see cref="RunPending"/> 执行，模拟界面线程的空闲时刻。
/// 可以从任何线程 <see cref="Post"/>（日志刷新的后台循环会这样做）。
/// </summary>
internal sealed class ManualUiDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public void Post(Action action)
    {
        lock (_gate)
        {
            _queue.Enqueue(action);
        }
    }

    /// <summary>按入队顺序执行全部待执行的动作，直到队列为空。</summary>
    public void RunPending()
    {
        while (true)
        {
            Action? next;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    return;
                }

                next = _queue.Dequeue();
            }

            next();
        }
    }
}
