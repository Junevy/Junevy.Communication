namespace Junevy.Communication.WebApi;

/// <summary>
/// 重试配置（P3-D10）。哪些结果与方法可以重试由库按固定规则判定：网络层失败、尝试超时、HTTP 408 / 429 / 500 / 502 / 503 / 504，
/// 且没有对端业务码；GET / HEAD / OPTIONS 自动可重试，POST / PUT / PATCH / DELETE 只有设置了幂等键或
/// <see cref="RetryUnsafeMethods"/> 为 true 时才重试。
/// </summary>
public sealed class WebApiRetryOptions
{
    /// <summary>首次尝试失败后最多再试几次，不能为负，默认 0（不重试）。</summary>
    public int RetryCount { get; set; }

    /// <summary>两次尝试之间的固定间隔（毫秒），不能为负，默认 1000。</summary>
    public int RetryDelay { get; set; } = 1000;

    /// <summary>是否允许重试没有幂等键的 POST / PUT / PATCH / DELETE，默认 false。重复上报的代价通常大于少一次重试，开启前请确认对端幂等。</summary>
    public bool RetryUnsafeMethods { get; set; }
}
