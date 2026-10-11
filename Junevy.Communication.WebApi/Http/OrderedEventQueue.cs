using System.Collections.Concurrent;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 单消费者的有序事件队列（内部，P3-D8）：多个线程可以同时入队，一个后台循环按入队顺序逐个交给消费者处理，
/// 因此并发请求与健康检查同时改变可用性时，事件不会乱序，慢的订阅者也不会拖慢请求。与 <c>ConnectionSupervisor</c> 的状态事件派发是同一种设计。
/// </summary>
/// <typeparam name="T">事件类型。</typeparam>
internal sealed class OrderedEventQueue<T>
{
    private readonly ConcurrentQueue<T> items = new ConcurrentQueue<T>();

    // 没有访问过 AvailableWaitHandle 的 SemaphoreSlim 不持有非托管资源；入队线程可能与完成并发，所以不释放它，避免 ObjectDisposedException。
    private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
    private readonly Action<T> consumer;
    private readonly AsyncLocal<bool> dispatching = new AsyncLocal<bool>();
    private readonly Task loop;
    private volatile bool completed;

    /// <summary>创建队列并启动消费循环。</summary>
    /// <param name="consumer">处理一个事件的回调；不得抛出异常（事件派发用 <c>EventSubscribers.InvokeEach</c> 隔离订阅者的异常）。</param>
    public OrderedEventQueue(Action<T> consumer)
    {
        this.consumer = consumer;
        loop = Task.Run(RunAsync);
    }

    /// <summary>当前代码是否正在消费者回调内执行（在事件处理器里释放客户端时，不能等待自己）。</summary>
    public bool IsDispatching => dispatching.Value;

    /// <summary>入队一个事件。完成之后的入队被忽略。</summary>
    /// <param name="item">事件。</param>
    public void Enqueue(T item)
    {
        if (completed)
            return;

        items.Enqueue(item);
        signal.Release();
    }

    /// <summary>
    /// 结束入队，等待已入队的事件处理完，最多 <paramref name="timeout"/> 毫秒；超时则丢弃剩余的事件。
    /// 在消费者回调内调用时不等待（否则会等自己），剩余事件在回调返回后继续处理。
    /// </summary>
    /// <param name="timeout">最长等待毫秒数。</param>
    /// <returns>被丢弃的事件数量。</returns>
    public async Task<int> CompleteAsync(int timeout)
    {
        completed = true;
        signal.Release();
        if (IsDispatching)
            return 0;

        Task finished = await Task.WhenAny(loop, Task.Delay(timeout)).ConfigureAwait(false);
        return finished == loop ? 0 : Discard();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await signal.WaitAsync().ConfigureAwait(false);
            if (items.TryDequeue(out T? item))
            {
                dispatching.Value = true;
                consumer(item);
                dispatching.Value = false;
            }
            else if (completed)
            {
                return;
            }
        }
    }

    private int Discard()
    {
        int dropped = 0;
        while (items.TryDequeue(out _))
            dropped++;

        return dropped;
    }
}
