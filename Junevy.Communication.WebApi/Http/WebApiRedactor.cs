namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 日志与事件参数的脱敏（内部，P3-D17）：配置中的请求头与查询参数、拦截器标记为敏感的请求头，值一律替换为 <see cref="Mask"/>。
/// 判定规则（大小写不敏感）在 <see cref="EndpointSettings"/> 里，这里只负责替换。
/// </summary>
internal sealed class WebApiRedactor
{
    /// <summary>替换敏感值的占位文本。</summary>
    public const string Mask = "***";

    private const char QueryPairSeparator = '&';
    private const char QueryValueSeparator = '=';

    private readonly EndpointSettings settings;

    /// <summary>创建脱敏器。</summary>
    /// <param name="settings">端点配置快照。</param>
    public WebApiRedactor(EndpointSettings settings)
    {
        this.settings = settings;
    }

    /// <summary>
    /// 返回脱敏后的请求头副本。
    /// </summary>
    /// <param name="headers">请求头。</param>
    /// <param name="sensitiveHeaders">拦截器标记为敏感的头名。</param>
    /// <returns>头名不区分大小写的副本。</returns>
    public IReadOnlyDictionary<string, string> RedactHeaders(IDictionary<string, string> headers, IEnumerable<string> sensitiveHeaders)
    {
        var sensitive = new HashSet<string>(sensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        var redacted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in headers)
            redacted[header.Key] = settings.IsRedactedHeader(header.Key) || sensitive.Contains(header.Key) ? Mask : header.Value;

        return redacted;
    }

    /// <summary>
    /// 返回脱敏后的 URI：配置的查询参数的值替换为 <see cref="Mask"/>，其余部分原样保留。没有需要脱敏的参数时返回原对象。
    /// </summary>
    /// <param name="uri">绝对 URI。</param>
    /// <returns>脱敏后的 URI（不含片段）。</returns>
    public Uri RedactUri(Uri uri)
    {
        string query = uri.Query;
        if (query.Length <= 1)
            return uri;

        string[] pairs = query.Substring(1).Split(QueryPairSeparator);
        bool changed = false;
        for (int i = 0; i < pairs.Length; i++)
        {
            int separator = pairs[i].IndexOf(QueryValueSeparator);
            if (separator < 0)
                continue;

            string name = Uri.UnescapeDataString(pairs[i].Substring(0, separator).Replace('+', ' '));
            if (settings.IsRedactedQueryParameter(name))
            {
                pairs[i] = pairs[i].Substring(0, separator + 1) + Mask;
                changed = true;
            }
        }

        return changed ? new Uri(uri.GetLeftPart(UriPartial.Path) + "?" + string.Join(QueryPairSeparator.ToString(), pairs)) : uri;
    }
}
