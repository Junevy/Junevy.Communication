namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 请求阶段拦截器的上下文：除请求数据外，可以调用 <see cref="Reject"/> 直接给出响应。
/// </summary>
public sealed class WebApiServerRequestingContext
{
    private Rejection? rejection;

    /// <summary>创建请求阶段的上下文（由服务器创建）。</summary>
    /// <param name="request">当前请求。</param>
    internal WebApiServerRequestingContext(WebApiServerContext request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    /// <summary>当前请求。</summary>
    public WebApiServerContext Request { get; }

    /// <summary>已拒绝时的详情；未拒绝时为 null。</summary>
    internal Rejection? RejectionDetails => rejection;

    /// <summary>
    /// 拒绝请求：之后的拦截器与处理器都不再执行，服务器直接返回 <paramref name="statusCode"/>。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码，必须是 400 至 599。</param>
    /// <param name="code">业务码（英文），写入包络的业务码字段或 Problem Details 的 <c>code</c> 扩展字段。</param>
    /// <param name="message">消息（英文），写入包络的消息字段或 Problem Details 的 <c>detail</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> 不在 400 至 599 之间。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> 或 <paramref name="message"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">本请求已经被拒绝过。</exception>
    public void Reject(int statusCode, string code, string message)
    {
        if (statusCode < 400 || statusCode > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "A rejection must use a 4xx or 5xx status code.");
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        if (rejection != null)
            throw new InvalidOperationException("The request has already been rejected.");

        rejection = new Rejection(statusCode, code, message);
    }

    /// <summary>拒绝的详情。</summary>
    /// <param name="StatusCode">HTTP 状态码。</param>
    /// <param name="Code">业务码。</param>
    /// <param name="Message">消息。</param>
    internal sealed record Rejection(int StatusCode, string Code, string Message);
}
