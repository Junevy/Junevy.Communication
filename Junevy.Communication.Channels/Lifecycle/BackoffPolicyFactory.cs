using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 把 <see cref="ReconnectOptions"/> 映射为 Core 的退避策略（计划 9.1）。
/// </summary>
internal static class BackoffPolicyFactory
{
    /// <summary>
    /// 创建退避策略。重连未启用时返回 null。参数非法时由 Core 的策略构造函数抛出 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    /// <param name="options">重连配置。</param>
    /// <returns>退避策略；未启用时为 null。</returns>
    public static IBackoffPolicy? Create(ReconnectOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        if (!options.Enabled)
            return null;

        switch (options.Mode)
        {
            case ReconnectMode.FixedInterval:
                return new FixedIntervalBackoff(options.Interval, options.MaxAttempts);

            case ReconnectMode.ExponentialBackoff:
                return new ExponentialBackoff(options.Interval, options.MaxInterval, 2.0, 0.2, options.MaxAttempts);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "Unknown reconnect mode.");
        }
    }
}
