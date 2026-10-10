namespace Junevy.Communication.Channels;

/// <summary>
/// 单次请求或接收的选项。
/// </summary>
public sealed class RequestOptions
{
    /// <summary>应答超时（毫秒）；为 null 时使用配置中的 RequestTimeout。</summary>
    public int? Timeout { get; set; }

    /// <summary>应答判定器；为 null 时按 <see cref="CorrelationMode"/> 的默认规则判定。Keyed 模式下忽略，按关联键匹配。</summary>
    public IResponseMatcher? Matcher { get; set; }
}
