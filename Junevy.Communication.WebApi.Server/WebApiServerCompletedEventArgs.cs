namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// <c>RequestCompleted</c> 的事件参数（P32-D10）：每个请求恰好触发一次，包括被拒绝、超时、对端断开与去重重放。
/// </summary>
public sealed class WebApiServerCompletedEventArgs : EventArgs
{
    /// <summary>创建事件参数（由服务器创建）。</summary>
    internal WebApiServerCompletedEventArgs(
        string serverName,
        long requestId,
        string method,
        string path,
        string? routeTemplate,
        int statusCode,
        WebApiServerOutcome outcome,
        string? code,
        string? message,
        Exception? exception,
        TimeSpan elapsed)
    {
        ServerName = serverName;
        RequestId = requestId;
        Method = method;
        Path = path;
        RouteTemplate = routeTemplate;
        StatusCode = statusCode;
        Outcome = outcome;
        Code = code;
        Message = message;
        Exception = exception;
        Elapsed = elapsed;
    }

    /// <summary>服务器名称。</summary>
    public string ServerName { get; }

    /// <summary>本服务器内递增的请求编号。</summary>
    public long RequestId { get; }

    /// <summary>HTTP 方法，大写。</summary>
    public string Method { get; }

    /// <summary>请求路径（不含查询串）。</summary>
    public string Path { get; }

    /// <summary>命中的路由模板；没有命中时为 null。</summary>
    public string? RouteTemplate { get; }

    /// <summary>写出的 HTTP 状态码；没有写出响应（对端已断开）时为 0。</summary>
    public int StatusCode { get; }

    /// <summary>结果分类。</summary>
    public WebApiServerOutcome Outcome { get; }

    /// <summary>业务码，或拒绝时的状态码文本；没有时为 null。</summary>
    public string? Code { get; }

    /// <summary>英文消息；没有时为 null。</summary>
    public string? Message { get; }

    /// <summary>底层异常，只供诊断，不参与决策。</summary>
    public Exception? Exception { get; }

    /// <summary>从收到请求到结束的耗时。</summary>
    public TimeSpan Elapsed { get; }
}
