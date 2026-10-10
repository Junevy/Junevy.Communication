using System.Diagnostics;

namespace Junevy.Communication.Testing;

/// <summary>
/// 耗时断言：执行操作并检查其耗时落在给定区间内。耗时断言按计划第 1 节第 5 条使用区间，而不是精确值。
/// </summary>
public static class TimingAssert
{
    /// <summary>
    /// 执行 <paramref name="action"/>，检查耗时在 [<paramref name="min"/>, <paramref name="max"/>] 之内。
    /// </summary>
    /// <param name="min">耗时下限（含）。</param>
    /// <param name="max">耗时上限（含）。</param>
    /// <param name="action">被计时的操作；其异常原样传播。</param>
    /// <returns>实际耗时。</returns>
    /// <exception cref="TimingOutOfRangeException">耗时超出区间。</exception>
    public static async Task<TimeSpan> WithinAsync(TimeSpan min, TimeSpan max, Func<Task> action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var stopwatch = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        stopwatch.Stop();

        TimeSpan elapsed = stopwatch.Elapsed;
        if (elapsed < min || elapsed > max)
            throw new TimingOutOfRangeException(elapsed, min, max);

        return elapsed;
    }
}

/// <summary>
/// 操作耗时超出 <see cref="TimingAssert.WithinAsync"/> 指定区间时抛出。
/// </summary>
public sealed class TimingOutOfRangeException : Exception
{
    /// <summary>
    /// 初始化异常。
    /// </summary>
    /// <param name="elapsed">实际耗时。</param>
    /// <param name="min">期望下限。</param>
    /// <param name="max">期望上限。</param>
    public TimingOutOfRangeException(TimeSpan elapsed, TimeSpan min, TimeSpan max)
        : base($"The operation took {elapsed.TotalMilliseconds:F0} ms, outside the expected range [{min.TotalMilliseconds:F0}, {max.TotalMilliseconds:F0}] ms.")
    {
        Elapsed = elapsed;
    }

    /// <summary>实际耗时。</summary>
    public TimeSpan Elapsed { get; }
}
