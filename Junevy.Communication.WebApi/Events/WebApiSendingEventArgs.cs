namespace Junevy.Communication.WebApi;

/// <summary>
/// <see cref="IWebApiEndpoint.RequestSending"/> 的参数：一次尝试即将发出的请求（拦截器处理之后、实际发送的内容）。
/// 自定义实现也可以用公开构造函数创建后引发事件。
/// </summary>
public sealed class WebApiSendingEventArgs : WebApiCallEventArgs
{
    /// <summary>创建参数，保存全部参数。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="callId">调用序号。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="requestUri">请求的绝对 URI（查询参数已脱敏）。</param>
    /// <param name="attempt">尝试序号（从 1 开始）。</param>
    /// <param name="headers">请求头，敏感头的值已替换为 <c>***</c>。</param>
    /// <param name="body">实际发送的正文字节。</param>
    /// <param name="contentType">正文的内容类型；没有时为 null。</param>
    /// <exception cref="ArgumentNullException">任一引用类型参数（<paramref name="contentType"/> 除外）为 null。</exception>
    public WebApiSendingEventArgs(string endpointName, long callId, string method, Uri requestUri, int attempt,
                                  IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> body, string? contentType)
        : base(endpointName, callId, method, requestUri)
    {
        Attempt = attempt;
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body;
        ContentType = contentType;
    }

    /// <summary>尝试序号：从 1 开始；重试与重放都加 1。</summary>
    public int Attempt { get; }

    /// <summary>请求头，敏感头的值已替换为 <c>***</c>。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>实际发送的正文字节。</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>正文的内容类型；没有时为 null。</summary>
    public string? ContentType { get; }
}
