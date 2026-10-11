using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// HTTP 结果的归类（内部，设计 15.7 与 P3-D13）：状态码与异常各一张表，分类供宿主做决策，不要匹配消息文本。
/// </summary>
internal static class HttpStatusClassifier
{
    private const int FirstSuccess = 200;
    private const int LastSuccess = 299;
    private const int Unauthorized = 401;
    private const int Forbidden = 403;
    private const int TooManyRequests = 429;

    /// <summary>
    /// 按状态码归类：2xx 为 <see cref="CommErrorKind.None"/>，401 / 403 为认证失败，429 为资源耗尽，其余（含 3xx，库不自动重定向）为对端错误。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <returns>分类。</returns>
    public static CommErrorKind Classify(int statusCode)
    {
        if (statusCode >= FirstSuccess && statusCode <= LastSuccess)
            return CommErrorKind.None;

        switch (statusCode)
        {
            case Unauthorized:
            case Forbidden:
                return CommErrorKind.AuthenticationFailed;
            case TooManyRequests:
                return CommErrorKind.ResourceExhausted;
            default:
                return CommErrorKind.RemoteError;
        }
    }

    /// <summary>
    /// 非 2xx 响应的固定格式英文信息：有原因短语时是 <c>HTTP 400 Bad Request.</c>，否则是 <c>HTTP 400.</c>。REST 与 SOAP 的解码共用。
    /// </summary>
    /// <param name="response">收到的响应。</param>
    /// <returns>信息文本。</returns>
    public static string Describe(HttpExchangeResponse response)
        => string.IsNullOrEmpty(response.ReasonPhrase) ? $"HTTP {response.StatusCode}." : $"HTTP {response.StatusCode} {response.ReasonPhrase}.";

    /// <summary>
    /// 把一次尝试中抛出的异常归类。作用域先判断：超时已触发为 <see cref="CommErrorKind.Timeout"/>，用户取消为 <see cref="CommErrorKind.Cancelled"/>
    /// （无论异常是什么，因为超时 / 取消会让正在进行的 I/O 以各种异常失败）；否则网络层异常为 <see cref="CommErrorKind.ConnectionClosed"/>，
    /// HTTP 栈自己报告的取消（作用域并未触发）视为超时，其余为 <see cref="CommErrorKind.Unspecified"/>。
    /// </summary>
    /// <param name="exception">异常。</param>
    /// <param name="scope">本次尝试的超时作用域。</param>
    /// <returns>分类与英文消息。</returns>
    public static (CommErrorKind Kind, string Message) FromException(Exception exception, TimeoutScope scope)
    {
        if (scope.IsTimedOut)
            return (CommErrorKind.Timeout, "The request attempt timed out.");
        if (scope.IsUserCancelled)
            return (CommErrorKind.Cancelled, "The request was cancelled.");
        if (exception is HttpRequestException || exception is IOException || exception is SocketException || exception is WebException)
            return (CommErrorKind.ConnectionClosed, $"The HTTP request failed: {exception.Message}");
        if (exception is OperationCanceledException)
            return (CommErrorKind.Timeout, "The HTTP stack reported that the request timed out.");

        return (CommErrorKind.Unspecified, $"The HTTP request failed with {exception.GetType().Name}: {exception.Message}");
    }
}
