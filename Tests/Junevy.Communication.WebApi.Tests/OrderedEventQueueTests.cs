using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="OrderedEventQueue{T}"/> 的测试：单消费者按入队顺序处理；完成时在限定时间内排空，超时的丢弃并报告数量。
/// 等待用 <see cref="TaskCompletionSource{TResult}"/>，不轮询、不睡眠。
/// </summary>
public sealed class OrderedEventQueueTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Items_AreConsumedInEnqueueOrder_OnASingleConsumer()
    {
        var seen = new List<int>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int total = 200;
        var queue = new OrderedEventQueue<int>(item =>
        {
            seen.Add(item);
            if (seen.Count == total)
                done.TrySetResult(true);
        });

        foreach (int item in Enumerable.Range(0, total))
            queue.Enqueue(item);
        await done.Task.WaitAsyncCompat(WaitLimit);

        Assert.Equal(Enumerable.Range(0, total), seen);
        Assert.Equal(0, await queue.CompleteAsync(5000));
    }

    [Fact]
    public async Task Items_EnqueuedConcurrently_AreNeverProcessedConcurrently()
    {
        int running = 0;
        int overlaps = 0;
        int processed = 0;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int total = 300;
        var queue = new OrderedEventQueue<int>(_ =>
        {
            if (Interlocked.Increment(ref running) > 1)
                Interlocked.Increment(ref overlaps);
            Interlocked.Decrement(ref running);
            if (Interlocked.Increment(ref processed) == total)
                done.TrySetResult(true);
        });

        await Task.WhenAll(Enumerable.Range(0, total).Select(i => Task.Run(() => queue.Enqueue(i))));
        await done.Task.WaitAsyncCompat(WaitLimit);

        Assert.Equal(0, overlaps);
    }

    [Fact]
    public async Task CompleteAsync_DrainsThePendingItemsAndReportsNothingDropped()
    {
        var seen = new List<int>();
        var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new OrderedEventQueue<int>(item =>
        {
            entered.TrySetResult(true);
            gate.Wait(WaitLimit);
            seen.Add(item);
        });
        queue.Enqueue(1);
        await entered.Task.WaitAsyncCompat(WaitLimit);
        queue.Enqueue(2);
        queue.Enqueue(3);

        Task<int> completing = queue.CompleteAsync(10000);
        gate.Set();

        Assert.Equal(0, await completing);
        Assert.Equal(new[] { 1, 2, 3 }, seen);
    }

    [Fact]
    public async Task CompleteAsync_WhenTheConsumerIsStuck_TimesOutDropsTheRestAndReportsTheCount()
    {
        var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>();
        var queue = new OrderedEventQueue<int>(item =>
        {
            entered.TrySetResult(true);
            gate.Wait(WaitLimit);
            seen.Add(item);
        });
        queue.Enqueue(1);
        await entered.Task.WaitAsyncCompat(WaitLimit);
        queue.Enqueue(2);
        queue.Enqueue(3);

        int dropped = await queue.CompleteAsync(50);
        gate.Set();

        Assert.Equal(2, dropped);
        await Task.Delay(100);
        Assert.Equal(new[] { 1 }, seen);
    }

    [Fact]
    public async Task Enqueue_AfterCompletion_IsIgnored()
    {
        var seen = new List<int>();
        var queue = new OrderedEventQueue<int>(seen.Add);
        await queue.CompleteAsync(5000);

        queue.Enqueue(1);
        await Task.Delay(50);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task CompleteAsync_CalledFromTheConsumer_DoesNotWaitForItself()
    {
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        OrderedEventQueue<int>? queue = null;
        queue = new OrderedEventQueue<int>(_ => queue!.CompleteAsync(60000).ContinueWith(completion => finished.TrySetResult(completion.Result)));

        queue.Enqueue(1);

        Assert.Equal(0, await finished.Task.WaitAsyncCompat(WaitLimit));
    }

    [Fact]
    public async Task IsDispatching_IsTrueOnlyInsideTheConsumer()
    {
        var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OrderedEventQueue<int>? queue = null;
        queue = new OrderedEventQueue<int>(_ => inside.TrySetResult(queue!.IsDispatching));

        Assert.False(queue.IsDispatching);
        queue.Enqueue(1);

        Assert.True(await inside.Task.WaitAsyncCompat(WaitLimit));
        Assert.False(queue.IsDispatching);
    }
}
