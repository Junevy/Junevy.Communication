namespace Junevy.Communication.Core.Resilience;

/// <summary>
/// 固定间隔退避：每次重试前等待相同的毫秒数。无状态，线程安全。
/// </summary>
public sealed class FixedIntervalBackoff : IBackoffPolicy
{
    private readonly int interval;
    private readonly int maxAttempts;

    /// <summary>
    /// 创建固定间隔退避策略。
    /// </summary>
    /// <param name="interval">每次重试前的等待毫秒数；不能为负。</param>
    /// <param name="maxAttempts">最大重试次数；0 表示无限。不能为负。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> 或 <paramref name="maxAttempts"/> 为负数。</exception>
    public FixedIntervalBackoff(int interval, int maxAttempts = 0)
    {
        if (interval < 0)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must not be negative.");
        if (maxAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "MaxAttempts must not be negative (0 means unlimited).");

        this.interval = interval;
        this.maxAttempts = maxAttempts;
    }

    /// <summary>
    /// 计算第 <paramref name="attempt"/> 次重试前的等待时间（固定为构造时的间隔；超过最大次数返回 null）。
    /// </summary>
    /// <param name="attempt">从 1 开始的重试序号。</param>
    /// <returns>该次重试前的等待毫秒数；超过最大次数时返回 null。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> 小于 1。</exception>
    public int? GetDelay(int attempt)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt must be at least 1.");

        if (maxAttempts > 0 && attempt > maxAttempts)
            return null;

        return interval;
    }
}
