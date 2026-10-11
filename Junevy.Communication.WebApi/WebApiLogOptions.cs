namespace Junevy.Communication.WebApi;

/// <summary>
/// 日志选项（P3-D17）：是否记录正文，以及哪些请求头与查询参数在日志和事件参数中只输出 <c>***</c>。
/// </summary>
public sealed class WebApiLogOptions
{
    /// <summary>是否在 Debug 日志中记录请求与响应正文，默认 false（正文可能包含业务数据或凭据）。</summary>
    public bool LogBodies { get; set; }

    /// <summary>记录正文时最多输出的字符数，超过的部分截断并注明原长度；不能为负，默认 2048。</summary>
    public int MaxLoggedBodyLength { get; set; } = 2048;

    /// <summary>值需要脱敏的请求头名（不区分大小写）。默认 <c>Authorization</c>、<c>Proxy-Authorization</c>。</summary>
    public string[] RedactedHeaders { get; set; } = { "Authorization", "Proxy-Authorization" };

    /// <summary>值需要脱敏的查询参数名（不区分大小写）。默认 <c>access_token</c>、<c>token</c>、<c>api_key</c>、<c>apikey</c>。</summary>
    public string[] RedactedQueryParameters { get; set; } = { "access_token", "token", "api_key", "apikey" };
}
