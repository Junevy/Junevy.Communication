using System.Globalization;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 重试规则（内部，P3-D10）：一个尝试的结果能不能重试、等多久。纯函数式，不碰网络与时钟（当前时间由调用方传入）。
/// 可重试的结果：网络层失败、尝试超时、HTTP 408 / 429 / 500 / 502 / 503 / 504，且没有对端业务码（业务失败说明请求已到达业务层，重试只会重复计数）。
/// 方法规则比 RFC 9110 更保守：GET / HEAD / OPTIONS 自动可重试；其余方法只有带幂等键或 <c>RetryUnsafeMethods</c> 为 true 时才重试。
/// </summary>
internal sealed class RetryPlanner
{
    /// <summary><c>Retry-After</c> 超过这个毫秒数就放弃重试（P3-D10）。</summary>
    public const int MaxRetryAfterMilliseconds = 30000;

    private const int RequestTimeout = 408;
    private const int TooManyRequests = 429;
    private const int InternalServerError = 500;
    private const int BadGateway = 502;
    private const int ServiceUnavailable = 503;
    private const int GatewayTimeout = 504;
    private const int MillisecondsPerSecond = 1000;

    private static readonly int[] RetryableStatusCodes = { RequestTimeout, TooManyRequests, InternalServerError, BadGateway, ServiceUnavailable, GatewayTimeout };
    private static readonly string[] SafeMethods = { "GET", "HEAD", "OPTIONS" };

    private readonly EndpointSettings settings;
    private readonly IBackoffPolicy backoff;

    /// <summary>创建规划器。</summary>
    /// <param name="settings">端点配置快照（重试次数与方法规则）。</param>
    /// <param name="backoff">覆盖固定间隔的退避策略；null 表示使用 <c>RetryDelay</c> 的固定间隔。</param>
    public RetryPlanner(EndpointSettings settings, IBackoffPolicy? backoff)
    {
        this.settings = settings;
        this.backoff = backoff ?? new FixedIntervalBackoff(settings.RetryDelay);
    }

    /// <summary>
    /// 判定要不要再试一次，以及再试之前等多久。
    /// </summary>
    /// <param name="retriesUsed">本次调用已经用掉的重试次数（拦截器请求的重放不计入）。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="hasIdempotencyKey">请求是否带幂等键。</param>
    /// <param name="kind">本次尝试结果的分类。</param>
    /// <param name="statusCode">HTTP 状态码；没有收到响应时为 null。</param>
    /// <param name="remoteCode">对端业务码；没有时为 null。</param>
    /// <param name="retryAfter">响应的 <c>Retry-After</c> 头原文；没有时为 null。</param>
    /// <param name="now">当前时间（解析 <c>Retry-After</c> 的 HTTP 日期用）。</param>
    /// <returns>再试之前等待的毫秒数；不再重试为 null。</returns>
    public int? NextDelay(int retriesUsed, string method, bool hasIdempotencyKey, CommErrorKind kind, int? statusCode,
                          string? remoteCode, string? retryAfter, DateTimeOffset now)
    {
        if (retriesUsed >= settings.RetryCount
            || !IsRetryableResult(kind, statusCode, remoteCode)
            || !IsRetryableMethod(method, hasIdempotencyKey))
            return null;

        int? delay = backoff.GetDelay(retriesUsed + 1);
        if (delay == null)
            return null;

        // Retry-After 只对"请你稍后再来"的两个状态有意义，且只能把等待拉长，不能缩短退避。
        if (statusCode != TooManyRequests && statusCode != ServiceUnavailable)
            return delay;

        int? requested = ParseRetryAfter(retryAfter, now);
        if (requested > MaxRetryAfterMilliseconds)
            return null;

        return requested.HasValue ? Math.Max(delay.Value, requested.Value) : delay;
    }

    /// <summary>
    /// 解析 <c>Retry-After</c>：非负整数秒，或 HTTP 日期（过去的日期按 0 处理）。
    /// </summary>
    /// <param name="value">头的原文。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>等待的毫秒数（超过 <see cref="int.MaxValue"/> 时取 <see cref="int.MaxValue"/>）；无法解析为 null。</returns>
    public static int? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        string? text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
            return ClampToInt(seconds * MillisecondsPerSecond);

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out DateTimeOffset date))
            return ClampToInt(Math.Max(0, (long)(date - now).TotalMilliseconds));

        return null;
    }

    private bool IsRetryableMethod(string method, bool hasIdempotencyKey)
        => hasIdempotencyKey || settings.RetryUnsafeMethods || SafeMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    private static bool IsRetryableResult(CommErrorKind kind, int? statusCode, string? remoteCode)
    {
        if (remoteCode != null)
            return false;

        return kind == CommErrorKind.ConnectionClosed
               || kind == CommErrorKind.Timeout
               || (statusCode.HasValue && RetryableStatusCodes.Contains(statusCode.Value));
    }

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)value;
}
