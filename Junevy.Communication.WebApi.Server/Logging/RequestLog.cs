using System.Text;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Logging;

/// <summary>
/// 每个请求的 Debug 日志（内部，P32-D15，与客户端的 <c>WebApiLogOptions</c> 同一形状）：
/// 请求进入管线时记方法、路径、脱敏后的查询参数与请求头；结束时记方法、路由模板、状态码、结果与耗时。
/// 正文只在 <c>LogBodies</c> 开启时记录，超过 <c>MaxLoggedBodyLength</c> 个字符截断并注明原长度。
/// <c>RedactedHeaders</c> 与 <c>RedactedQueryParameters</c>（不区分大小写）的值只输出 <see cref="Mask"/>。只在 Debug 启用时才生成文本。
/// </summary>
internal sealed class RequestLog
{
    /// <summary>替换敏感值的占位文本。</summary>
    public const string Mask = "***";

    private readonly WebApiLogOptions options;
    private readonly HashSet<string> redactedHeaders;
    private readonly HashSet<string> redactedQuery;
    private readonly ILogger logger;

    /// <summary>创建请求日志。</summary>
    /// <param name="options">已校验的日志选项（副本）。</param>
    /// <param name="logger">日志记录器。</param>
    public RequestLog(WebApiLogOptions options, ILogger logger)
    {
        this.options = options;
        this.logger = logger;
        redactedHeaders = new HashSet<string>(options.RedactedHeaders, StringComparer.OrdinalIgnoreCase);
        redactedQuery = new HashSet<string>(options.RedactedQueryParameters, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>记录进入管线的请求（正文已经读取）。</summary>
    /// <param name="requestId">请求编号。</param>
    /// <param name="raw">请求快照。</param>
    /// <param name="body">请求正文。</param>
    public void LogRequest(long requestId, RawRequest raw, byte[] body)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        logger.LogDebug("Request {RequestId}: {Method} {Path}{Query} headers: {Headers}",
            requestId, raw.Method, raw.Path, FormatQuery(raw.Query), FormatHeaders(raw.Headers));
        if (options.LogBodies && body.Length > 0)
            logger.LogDebug("Request {RequestId} body: {Body}", requestId, FormatBody(body));
    }

    /// <summary>记录请求的结束。</summary>
    /// <param name="requestId">请求编号。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="route">命中的路由模板；没有命中时为请求路径。</param>
    /// <param name="result">请求的结果。</param>
    /// <param name="elapsed">耗时。</param>
    public void LogCompleted(long requestId, string method, string route, ServerResult result, TimeSpan elapsed)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        logger.LogDebug("Request {RequestId} {Method} {Route} finished with {StatusCode} ({Outcome}) in {ElapsedMs} ms.",
            requestId, method, route, result.StatusCode, result.Outcome, (long)elapsed.TotalMilliseconds);
        if (options.LogBodies && result.Response?.Body is { Length: > 0 } body)
            logger.LogDebug("Request {RequestId} response body: {Body}", requestId, FormatBody(body));
    }

    /// <summary>脱敏后的查询串（以 <c>?</c> 开头）；没有查询参数时为空串。</summary>
    /// <param name="query">查询参数。</param>
    /// <returns>查询串。</returns>
    internal string FormatQuery(IReadOnlyDictionary<string, string> query)
        => query.Count == 0
            ? string.Empty
            : "?" + string.Join("&", query.Select(pair => pair.Key + "=" + (redactedQuery.Contains(pair.Key) ? Mask : pair.Value)));

    /// <summary>脱敏后的请求头，形如 <c>Name: value; Authorization: ***</c>。</summary>
    /// <param name="headers">请求头。</param>
    /// <returns>请求头文本。</returns>
    internal string FormatHeaders(IReadOnlyDictionary<string, string> headers)
        => string.Join("; ", headers.Select(pair => pair.Key + ": " + (redactedHeaders.Contains(pair.Key) ? Mask : pair.Value)));

    // 正文按 UTF-8 解码；超过 MaxLoggedBodyLength 个字符的部分截断，并注明原长度（与客户端一致）。
    private string FormatBody(byte[] body)
    {
        string text = Encoding.UTF8.GetString(body);
        return text.Length <= options.MaxLoggedBodyLength
            ? text
            : $"{text[..options.MaxLoggedBodyLength]}... (truncated, {text.Length} characters in total)";
    }
}
