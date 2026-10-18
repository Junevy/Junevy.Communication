using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi;

/// <summary>
/// <see cref="IWebApiEndpoint.ResponseReceived"/> 的参数：一次尝试的结果（含网络层失败）以及是否会重试。
/// 自定义实现也可以用公开构造函数创建后引发事件。
/// </summary>
public sealed class WebApiResponseEventArgs : WebApiCallEventArgs
{
    /// <summary>创建参数，保存全部参数。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="callId">调用序号。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="requestUri">本次尝试请求的绝对 URI（查询参数已脱敏）。</param>
    /// <param name="attempt">尝试序号（从 1 开始）。</param>
    /// <param name="statusCode">HTTP 状态码；网络层失败时为 null。</param>
    /// <param name="errorKind">本次尝试的归类；成功为 <see cref="CommErrorKind.None"/>。</param>
    /// <param name="body">拦截器处理后的响应正文；没有响应时为空。</param>
    /// <param name="elapsed">本次尝试从开始（含拦截器）到得出结果的耗时。</param>
    /// <param name="willRetry">是否会再试一次（重试或拦截器请求的重放）。</param>
    /// <param name="retryDelay">再试之前的等待毫秒数；不再试时为 0。</param>
    /// <param name="exception">底层异常，仅供诊断；没有时为 null。</param>
    /// <exception cref="ArgumentNullException">任一必需的引用类型参数为 null。</exception>
    public WebApiResponseEventArgs(string endpointName, long callId, string method, Uri requestUri, int attempt, int? statusCode,
                                   CommErrorKind errorKind, ReadOnlyMemory<byte> body, TimeSpan elapsed, bool willRetry,
                                   int retryDelay, Exception? exception)
        : base(endpointName, callId, method, requestUri)
    {
        Attempt = attempt;
        StatusCode = statusCode;
        ErrorKind = errorKind;
        Body = body;
        Elapsed = elapsed;
        WillRetry = willRetry;
        RetryDelay = retryDelay;
        Exception = exception;
    }

    /// <summary>尝试序号：从 1 开始；重试与重放都加 1。</summary>
    public int Attempt { get; }

    /// <summary>HTTP 状态码；网络层失败时为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>本次尝试的归类；成功为 <see cref="CommErrorKind.None"/>。</summary>
    public CommErrorKind ErrorKind { get; }

    /// <summary>拦截器处理后的响应正文；没有响应时为空。</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>本次尝试从开始（含拦截器）到得出结果的耗时。</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>是否会再试一次（重试或拦截器请求的重放）。</summary>
    public bool WillRetry { get; }

    /// <summary>再试之前的等待毫秒数；不再试时为 0。</summary>
    public int RetryDelay { get; }

    /// <summary>底层异常，仅供诊断；没有时为 null。</summary>
    public Exception? Exception { get; }
}
