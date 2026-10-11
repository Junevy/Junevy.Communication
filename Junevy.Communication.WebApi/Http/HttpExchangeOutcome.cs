using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 一次 HTTP 交换的结果（内部）：要么收到了响应（任何状态码，包括 4xx / 5xx），要么在得到完整响应之前失败（网络层、超时、取消、
/// 请求非法、正文超限）。非 2xx 状态码属于"收到响应"，由协议层解释。
/// </summary>
internal sealed class HttpExchangeOutcome
{
    private readonly int? failureStatusCode;

    private HttpExchangeOutcome(HttpExchangeResponse? response, CommErrorKind failureKind, string? failureMessage, Exception? exception, int? failureStatusCode)
    {
        Response = response;
        FailureKind = failureKind;
        FailureMessage = failureMessage;
        Exception = exception;
        this.failureStatusCode = failureStatusCode;
    }

    /// <summary>收到的响应；失败时为 null。</summary>
    public HttpExchangeResponse? Response { get; }

    /// <summary>失败分类；收到响应时为 <see cref="CommErrorKind.None"/>。</summary>
    public CommErrorKind FailureKind { get; }

    /// <summary>失败信息（英文）；收到响应时为 null。</summary>
    public string? FailureMessage { get; }

    /// <summary>底层异常，仅供诊断；没有时为 null。</summary>
    public Exception? Exception { get; }

    /// <summary>已知的 HTTP 状态码：收到响应时为响应的状态码；失败时仅在响应头已到达后才失败（正文超限）才有值，否则为 null。</summary>
    public int? StatusCode => Response?.StatusCode ?? failureStatusCode;

    /// <summary>创建"收到响应"的结果。</summary>
    /// <param name="response">响应。</param>
    /// <returns>结果。</returns>
    public static HttpExchangeOutcome Received(HttpExchangeResponse response)
        => new HttpExchangeOutcome(response, CommErrorKind.None, null, null, null);

    /// <summary>创建失败的结果。</summary>
    /// <param name="kind">失败分类。</param>
    /// <param name="message">失败信息（英文）。</param>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <param name="statusCode">已收到的状态码；没有时为 null。</param>
    /// <returns>结果。</returns>
    public static HttpExchangeOutcome Failed(CommErrorKind kind, string message, Exception? exception = null, int? statusCode = null)
        => new HttpExchangeOutcome(null, kind, message, exception, statusCode);
}
