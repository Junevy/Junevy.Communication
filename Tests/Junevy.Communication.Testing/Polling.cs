using System.Diagnostics;

namespace Junevy.Communication.Testing;

/// <summary>
/// 轮询等待：反复检查一个条件，直到它成立或时限用尽。用它代替各测试项目里各自的 <c>WaitUntilAsync</c>，
/// 共享库不引用 xUnit 的断言，因此超时抛 <see cref="TimeoutException"/>（对 xUnit 而言与断言失败等价）。
/// </summary>
public static class Polling
{
    /// <summary>条件检查的间隔（毫秒）。</summary>
    private const int PollIntervalMs = 5;

    /// <summary>未指定时限时的默认值（毫秒）。</summary>
    public const int DefaultTimeoutMs = 10000;

    /// <summary>轮询直到 <paramref name="condition"/> 成立；时限用尽仍未成立则失败。</summary>
    /// <param name="condition">要检查的条件；每次轮询调用一次。</param>
    /// <param name="milliseconds">时限（毫秒）；到期前会再判一次条件，因此条件恰好在时限附近成立也能通过。</param>
    /// <returns>条件成立即返回的任务。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="milliseconds"/> 为负数。</exception>
    /// <exception cref="TimeoutException">时限内条件始终不成立。</exception>
    public static async Task WaitUntilAsync(Func<bool> condition, int milliseconds = DefaultTimeoutMs)
    {
        if (condition == null)
            throw new ArgumentNullException(nameof(condition));
        if (milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "The time limit must not be negative.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            if (condition())
                return;

            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
        }

        // 时限用尽后再判一次：条件可能恰好在最后一次等待期间成立。
        if (condition())
            return;

        throw new TimeoutException($"The condition was not met within {milliseconds} ms.");
    }
}