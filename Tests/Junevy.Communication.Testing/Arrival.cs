namespace Junevy.Communication.Testing;

/// <summary>
/// 时限等待：等一个已经启动的操作在给定时间内结束。用它代替各测试项目里各自的"任务在时限内完成，否则失败"助手
/// （<c>WithinAsync</c> / <c>AwaitWithinAsync</c> / <c>FinishWithinAsync</c> / <c>ArrivesAsync</c>）。
/// </summary>
/// <remarks>
/// 按时完成就返回该操作的结果；它自己的异常（取消、协议错误等）原样抛出，调用方看到的是真正的失败原因而不是超时。
/// 超时抛 <see cref="TimeoutException"/>——共享库不引用 xUnit 的断言，但对 xUnit 而言抛异常与断言失败等价。
/// </remarks>
public static class Arrival
{
    /// <summary>等待 <paramref name="pending"/> 结束并返回它的结果；时限内没结束就失败。</summary>
    /// <typeparam name="T">结果的类型。</typeparam>
    /// <param name="pending">已经启动的操作。</param>
    /// <param name="milliseconds">时限（毫秒）。</param>
    /// <returns>该操作的结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pending"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="milliseconds"/> 为负数。</exception>
    /// <exception cref="TimeoutException">时限内操作没有结束。</exception>
    public static async Task<T> WithinAsync<T>(Task<T> pending, int milliseconds)
    {
        if (pending == null)
            throw new ArgumentNullException(nameof(pending));
        ValidateLimit(milliseconds);

        await WaitOrTimeoutAsync(pending, milliseconds).ConfigureAwait(false);

        // 这里不返回 WhenAny 的赢家，而是重新 await 原任务：这样任务自身的异常原样抛出，
        // 而不是被 WhenAny 吞掉或换成超时。
        return await pending.ConfigureAwait(false);
    }

    /// <summary>等待 <paramref name="pending"/> 结束；时限内没结束就失败。</summary>
    /// <param name="pending">已经启动的操作。</param>
    /// <param name="milliseconds">时限（毫秒）。</param>
    /// <returns>等任务结束后返回的任务。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pending"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="milliseconds"/> 为负数。</exception>
    /// <exception cref="TimeoutException">时限内操作没有结束。</exception>
    public static async Task WithinAsync(Task pending, int milliseconds)
    {
        if (pending == null)
            throw new ArgumentNullException(nameof(pending));
        ValidateLimit(milliseconds);

        await WaitOrTimeoutAsync(pending, milliseconds).ConfigureAwait(false);

        await pending.ConfigureAwait(false);
    }

    private static async Task WaitOrTimeoutAsync(Task pending, int milliseconds)
    {
        await Task.WhenAny(pending, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (!pending.IsCompleted)
            throw new TimeoutException($"The awaited operation did not complete within {milliseconds} ms.");
    }

    private static void ValidateLimit(int milliseconds)
    {
        if (milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "The time limit must not be negative.");
    }
}