namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 响应阶段拦截器的上下文：告诉拦截器将要写出的状态码与分类，并允许追加响应头。
/// </summary>
public sealed class WebApiServerRespondingContext
{
    /// <summary>创建响应阶段的上下文（由服务器创建）。</summary>
    /// <param name="request">当前请求。</param>
    /// <param name="statusCode">将要写出的 HTTP 状态码。</param>
    /// <param name="outcome">本次请求的结果分类。</param>
    /// <param name="headers">可追加的响应头（名称不区分大小写）。</param>
    internal WebApiServerRespondingContext(WebApiServerContext request, int statusCode, WebApiServerOutcome outcome, IDictionary<string, string> headers)
    {
        Request = request;
        StatusCode = statusCode;
        Outcome = outcome;
        Headers = headers;
    }

    /// <summary>当前请求。</summary>
    public WebApiServerContext Request { get; }

    /// <summary>将要写出的 HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>本次请求的结果分类。</summary>
    public WebApiServerOutcome Outcome { get; }

    /// <summary>可追加的响应头；与服务器自己写出的同名头重复时以拦截器的值为准。</summary>
    public IDictionary<string, string> Headers { get; }
}
