using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi;

/// <summary>
/// 一次尝试收到 HTTP 响应后的上下文，传给 <see cref="IWebApiInterceptor.OnReceivedAsync"/>。
/// 上下文只<b>记录意图</b>（<see cref="Fail"/>、<see cref="RequestReplay"/>），由尝试循环读取。
/// </summary>
public sealed class WebApiReceivedContext
{
    private byte[] body;

    /// <summary>创建接收上下文，保存全部参数（按引用保存）。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="callId">调用序号。</param>
    /// <param name="attempt">尝试序号。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="requestUri">本次尝试实际请求的绝对 URI。</param>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="responseHeaders">响应头；同名多值用 ", " 连接。</param>
    /// <param name="body">响应正文。</param>
    /// <param name="contentType">响应的内容类型；没有时为 null。</param>
    /// <param name="items">本次调用内所有拦截器、所有尝试共享的数据。</param>
    /// <exception cref="ArgumentNullException">任一必需参数为 null。</exception>
    public WebApiReceivedContext(string endpointName, long callId, int attempt, string method, Uri requestUri, int statusCode,
                                 IReadOnlyDictionary<string, string> responseHeaders, byte[] body, string? contentType,
                                 IDictionary<string, object?> items)
    {
        EndpointName = endpointName ?? throw new ArgumentNullException(nameof(endpointName));
        CallId = callId;
        Attempt = attempt;
        Method = method ?? throw new ArgumentNullException(nameof(method));
        RequestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        StatusCode = statusCode;
        ResponseHeaders = responseHeaders ?? throw new ArgumentNullException(nameof(responseHeaders));
        this.body = body ?? throw new ArgumentNullException(nameof(body));
        ContentType = contentType;
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    /// <summary>端点名称。</summary>
    public string EndpointName { get; }

    /// <summary>调用序号：同一客户端内单调递增，从 1 开始。</summary>
    public long CallId { get; }

    /// <summary>尝试序号：从 1 开始；重试与重放都加 1。</summary>
    public int Attempt { get; }

    /// <summary>HTTP 方法。</summary>
    public string Method { get; }

    /// <summary>本次尝试实际请求的绝对 URI。</summary>
    public Uri RequestUri { get; }

    /// <summary>本次调用内所有拦截器、所有尝试共享的数据。</summary>
    public IDictionary<string, object?> Items { get; }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>响应头；同名多值用 ", " 连接。</summary>
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; }

    /// <summary>响应正文，可以替换（例如解密）；之后的拦截器与协议层看到的是替换后的字节。</summary>
    /// <exception cref="ArgumentNullException">设置为 null。</exception>
    public byte[] Body
    {
        get => body;
        set => body = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>响应的内容类型；没有时为 null。</summary>
    public string? ContentType { get; }

    /// <summary>拦截器通过 <see cref="Fail"/> 记录的失败意图；没有时为 null。</summary>
    internal CallFailure? Failure { get; private set; }

    /// <summary>拦截器是否调用了 <see cref="RequestReplay"/>。</summary>
    internal bool ReplayRequested { get; private set; }

    /// <summary>
    /// 请求立即再发一次（重新执行 <see cref="IWebApiInterceptor.OnSendingAsync"/>），例如 401 后换了 token。
    /// 每次调用最多重放 1 次，不计入重试次数，也不受方法规则限制（拦截器对此负责）；与 <see cref="Fail"/> 同时出现时以 <see cref="Fail"/> 为准。
    /// </summary>
    public void RequestReplay() => ReplayRequested = true;

    /// <summary>
    /// 把本次结果改判为失败：不重试，调用以指定的结果结束（保留状态码）。多次调用时只记录第一次。
    /// </summary>
    /// <param name="message">失败信息（英文）；不能为 null。</param>
    /// <param name="kind">失败分类；不允许为 <see cref="CommErrorKind.None"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> 为 <see cref="CommErrorKind.None"/>。</exception>
    public void Fail(string message, CommErrorKind kind)
    {
        CallFailure failure = CallFailure.Create(message, kind);
        Failure ??= failure;
    }
}
