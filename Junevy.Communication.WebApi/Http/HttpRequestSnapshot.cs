namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 一次调用的完整、可重放的请求快照（内部，P3-D5）：方法、相对 URI（含已编码的查询）、头、内容类型、正文与幂等键。
/// 管线每次尝试都从快照重建请求上下文，因此拦截器的修改不会累积；离线缓存以后也从它重放。快照创建后不可变。
/// </summary>
internal sealed class HttpRequestSnapshot
{
    private HttpRequestSnapshot(string method, string relativeUri, IReadOnlyDictionary<string, string> headers, string? contentType,
                                byte[] body, string? idempotencyKey)
    {
        Method = method;
        RelativeUri = relativeUri;
        Headers = headers;
        ContentType = contentType;
        Body = body;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>HTTP 方法。</summary>
    public string Method { get; }

    /// <summary>相对于端点基地址的 URI，含已编码的查询；基地址本身（SOAP）为空串。</summary>
    public string RelativeUri { get; }

    /// <summary>合并后的请求头（头名不区分大小写）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>正文的内容类型；没有时为 null。</summary>
    public string? ContentType { get; }

    /// <summary>请求正文。快照不复制它，调用方与使用方都不得修改其内容（每次尝试交给拦截器的是副本）。</summary>
    public byte[] Body { get; }

    /// <summary>幂等键；没有时为 null。</summary>
    public string? IdempotencyKey { get; }

    /// <summary>
    /// 创建快照。头的合并顺序：配置的默认头 → 请求自带的头（同名覆盖）→ 幂等键（配置了幂等头名且设置了幂等键时）。
    /// </summary>
    /// <param name="settings">端点配置快照。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="relativeUri">相对 URI（含已编码的查询）。</param>
    /// <param name="requestHeaders">请求自带的头；没有时为 null。</param>
    /// <param name="contentType">正文的内容类型；没有时为 null。</param>
    /// <param name="body">请求正文。</param>
    /// <param name="idempotencyKey">幂等键；没有时为 null。</param>
    /// <returns>请求快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/>、<paramref name="relativeUri"/> 或 <paramref name="body"/> 为 null。</exception>
    public static HttpRequestSnapshot Create(EndpointSettings settings, string method, string relativeUri,
                                             IDictionary<string, string>? requestHeaders, string? contentType, byte[] body, string? idempotencyKey)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));
        if (relativeUri == null)
            throw new ArgumentNullException(nameof(relativeUri));
        if (body == null)
            throw new ArgumentNullException(nameof(body));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in settings.DefaultHeaders.Concat(requestHeaders ?? new Dictionary<string, string>()))
            headers[header.Key] = header.Value;

        if (idempotencyKey != null && settings.IdempotencyHeaderName != null)
            headers[settings.IdempotencyHeaderName] = idempotencyKey;

        return new HttpRequestSnapshot(method, relativeUri, headers, contentType, body, idempotencyKey);
    }
}
