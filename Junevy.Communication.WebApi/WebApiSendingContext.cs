using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi;

/// <summary>
/// 一次发送尝试的请求上下文，传给 <see cref="IWebApiInterceptor.OnSendingAsync"/>。每次尝试都从请求快照重新创建，
/// 因此上一次尝试中拦截器的修改不会累积（避免重复签名）；<see cref="Items"/> 是整个调用共享的。
/// 上下文只<b>记录意图</b>（<see cref="Fail"/>、<see cref="MarkSensitiveHeader"/>），由尝试循环读取。
/// </summary>
public sealed class WebApiSendingContext
{
    private readonly HashSet<string> sensitiveHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private Uri requestUri;
    private byte[] body;

    /// <summary>创建发送上下文，保存全部参数（头字典被复制为不区分大小写的副本，其余按引用保存）。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="callId">调用序号（同一客户端内单调递增，从 1 开始）。</param>
    /// <param name="attempt">尝试序号（从 1 开始；重试与重放都加 1）。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="requestUri">请求的绝对 URI。</param>
    /// <param name="headers">请求头。</param>
    /// <param name="body">请求正文。</param>
    /// <param name="contentType">正文的内容类型；没有时为 null。</param>
    /// <param name="idempotencyKey">幂等键；没有时为 null。</param>
    /// <param name="items">本次调用内所有拦截器、所有尝试共享的数据。</param>
    /// <exception cref="ArgumentNullException">任一必需参数为 null。</exception>
    public WebApiSendingContext(string endpointName, long callId, int attempt, string method, Uri requestUri,
                                IDictionary<string, string> headers, byte[] body, string? contentType,
                                string? idempotencyKey, IDictionary<string, object?> items)
    {
        EndpointName = endpointName ?? throw new ArgumentNullException(nameof(endpointName));
        CallId = callId;
        Attempt = attempt;
        Method = method ?? throw new ArgumentNullException(nameof(method));
        this.requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        Headers = new Dictionary<string, string>(headers ?? throw new ArgumentNullException(nameof(headers)), StringComparer.OrdinalIgnoreCase);
        this.body = body ?? throw new ArgumentNullException(nameof(body));
        ContentType = contentType;
        IdempotencyKey = idempotencyKey;
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

    /// <summary>
    /// 请求的绝对 URI。可以修改（例如追加签名参数），但 scheme 与 authority 不能变，否则本次调用以 <c>InvalidRequest</c> 失败（P3-D17）。
    /// </summary>
    /// <exception cref="ArgumentNullException">设置为 null。</exception>
    public Uri RequestUri
    {
        get => requestUri;
        set => requestUri = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>请求头，名称不区分大小写，可以修改。</summary>
    public IDictionary<string, string> Headers { get; }

    /// <summary>请求正文，可以替换（例如加密）。</summary>
    /// <exception cref="ArgumentNullException">设置为 null。</exception>
    public byte[] Body
    {
        get => body;
        set => body = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>正文的内容类型；没有时为 null。</summary>
    public string? ContentType { get; set; }

    /// <summary>幂等键；没有时为 null。</summary>
    public string? IdempotencyKey { get; }

    /// <summary>本次调用内所有拦截器、所有尝试共享的数据。</summary>
    public IDictionary<string, object?> Items { get; }

    /// <summary>拦截器通过 <see cref="Fail"/> 记录的失败意图；没有时为 null。</summary>
    internal CallFailure? Failure { get; private set; }

    /// <summary>被标记为敏感的头名（不区分大小写）。</summary>
    internal IReadOnlyCollection<string> SensitiveHeaders => sensitiveHeaders;

    /// <summary>
    /// 把指定头标记为敏感：日志与事件参数中该头的值只输出 <c>***</c>。
    /// </summary>
    /// <param name="name">头名。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空。</exception>
    public void MarkSensitiveHeader(string name)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));
        if (name.Length == 0)
            throw new ArgumentException("The header name must not be empty.", nameof(name));

        sensitiveHeaders.Add(name);
    }

    /// <summary>
    /// 中止本次调用：不发送、不重试，调用以指定的结果结束。多次调用时只记录第一次。
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
