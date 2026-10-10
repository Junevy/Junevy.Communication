namespace Junevy.Communication.Core.Resilience;

/// <summary>
/// 指数退避（带抖动）。第 n 次重试的基础等待为 <c>min(maxInterval, initialInterval × multiplier^(n−1))</c>，
/// 实际等待为 <c>基础等待 × (1 + jitter × (2r − 1))</c>（r ∈ [0, 1)），结果取整并夹在 [0, maxInterval] 之内。
/// 线程安全：随机数访问加锁（net472 的 <see cref="Random"/> 不是线程安全的）。
/// </summary>
public sealed class ExponentialBackoff : IBackoffPolicy
{
    private readonly int initialInterval;
    private readonly int maxInterval;
    private readonly double multiplier;
    private readonly double jitter;
    private readonly int maxAttempts;
    private readonly Random random;
    private readonly object randomLock = new object();

    /// <summary>
    /// 创建指数退避策略。
    /// </summary>
    /// <param name="initialInterval">第 1 次重试的基础等待毫秒数；不能为负。</param>
    /// <param name="maxInterval">基础等待的上限毫秒数；不能小于 <paramref name="initialInterval"/>。</param>
    /// <param name="multiplier">每次重试的增长倍数；必须是不小于 1 的有限值。</param>
    /// <param name="jitter">抖动幅度，取值 [0, 1]；0 表示不抖动。</param>
    /// <param name="maxAttempts">最大重试次数；0 表示无限。不能为负。</param>
    /// <param name="random">随机源；为 null 时新建一个。测试可注入固定种子。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一参数超出范围。</exception>
    public ExponentialBackoff(int initialInterval, int maxInterval, double multiplier = 2.0, double jitter = 0.2,
                              int maxAttempts = 0, Random? random = null)
    {
        if (initialInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(initialInterval), initialInterval, "InitialInterval must not be negative.");
        if (maxInterval < initialInterval)
            throw new ArgumentOutOfRangeException(nameof(maxInterval), maxInterval, "MaxInterval must not be less than InitialInterval.");
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier < 1.0)
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "Multiplier must be a finite value of at least 1.");
        if (double.IsNaN(jitter) || jitter < 0.0 || jitter > 1.0)
            throw new ArgumentOutOfRangeException(nameof(jitter), jitter, "Jitter must be within [0, 1].");
        if (maxAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "MaxAttempts must not be negative (0 means unlimited).");

        this.initialInterval = initialInterval;
        this.maxInterval = maxInterval;
        this.multiplier = multiplier;
        this.jitter = jitter;
        this.maxAttempts = maxAttempts;
        this.random = random ?? new Random();
    }

    /// <summary>
    /// 计算第 <paramref name="attempt"/> 次重试前的等待时间（指数增长、夹紧上限、叠加抖动）。
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

        // 先在浮点域夹紧再转换，避免指数增长后的大值在强转 int 时溢出。
        double baseDelay = initialInterval == 0
            ? 0d
            : Math.Min(maxInterval, initialInterval * Math.Pow(multiplier, attempt - 1));

        double factor;
        lock (randomLock)
        {
            factor = 1.0 + jitter * (2.0 * random.NextDouble() - 1.0);
        }

        double delay = Math.Round(baseDelay * factor, MidpointRounding.AwayFromZero);
        if (delay < 0d)
            delay = 0d;
        else if (delay > maxInterval)
            delay = maxInterval;

        return (int)delay;
    }
}
