namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 测试里等待任务的扩展：net472 没有 <c>Task.WaitAsync</c>，而且测试只允许"等待某个信号，超时即失败"这一种等法（不轮询、不睡眠）。
/// </summary>
internal static class TaskWaitExtensions
{
    /// <summary>等待任务完成，最多 <paramref name="limit"/>；超时抛出 <see cref="TimeoutException"/>。</summary>
    public static async Task<T> WaitAsyncCompat<T>(this Task<T> task, TimeSpan limit)
    {
        await WaitAsyncCompat((Task)task, limit);
        return await task;
    }

    /// <summary>等待任务完成，最多 <paramref name="limit"/>；超时抛出 <see cref="TimeoutException"/>。</summary>
    public static async Task WaitAsyncCompat(this Task task, TimeSpan limit)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(limit));
        if (finished != task)
            throw new TimeoutException("The awaited task did not complete in time.");

        await task;
    }
}
