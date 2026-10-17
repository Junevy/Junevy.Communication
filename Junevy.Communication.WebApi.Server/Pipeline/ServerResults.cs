using System.Globalization;
using Junevy.Communication.WebApi.Server.Formatting;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 管线与入口共用的结果构造（内部）：框架与管线的拒绝、并发或缓存已满的 503、未预期异常的 500、超时的 504、对端断开。
/// 所有这些结果都经同一个格式化器写成线上格式，并在这里写对应级别的日志（P32-D15：拒绝 Warning、异常 Error、断开 Debug）。
/// </summary>
internal sealed class ServerResults
{
    private const string UnexpectedErrorMessage = "An unexpected error occurred.";
    private const string TimeoutMessage = "The request timed out.";
    private const string RetryAfterHeader = "Retry-After";

    private readonly ResponseFormatter formatter;
    private readonly ILogger logger;

    /// <summary>创建结果构造。</summary>
    /// <param name="formatter">响应格式化器。</param>
    /// <param name="logger">日志记录器。</param>
    public ServerResults(ResponseFormatter formatter, ILogger logger)
    {
        this.formatter = formatter;
        this.logger = logger;
    }

    /// <summary>拒绝，业务码为状态码文本（404、405、409、422 等）。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="message">英文消息。</param>
    /// <param name="headers">额外的响应头。</param>
    /// <param name="requestId">请求编号（只用于日志）。</param>
    /// <returns>拒绝结果。</returns>
    public ServerResult Reject(int statusCode, string message, IDictionary<string, string> headers, long requestId)
        => Refusal(statusCode, Text(statusCode), message, headers, null, requestId);

    /// <summary>正文阶段的拒绝（读取或解码：400、408、413、415），底层异常只进 Debug 日志。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="message">英文消息。</param>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <param name="requestId">请求编号。</param>
    /// <returns>拒绝结果。</returns>
    public ServerResult RejectBody(int statusCode, string message, Exception? exception, long requestId)
        => Refusal(statusCode, Text(statusCode), message, new Dictionary<string, string>(), exception, requestId);

    /// <summary>并发或去重缓存已满时的 503（带 <c>Retry-After: 1</c>）。</summary>
    /// <param name="message">英文消息。</param>
    /// <param name="requestId">请求编号。</param>
    /// <returns>拒绝结果。</returns>
    public ServerResult Busy(string message, long requestId)
        => Reject(503, message, new Dictionary<string, string> { [RetryAfterHeader] = "1" }, requestId);

    /// <summary>拒绝，业务码由调用方给出（拦截器的 <c>Reject</c>）。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">业务码。</param>
    /// <param name="message">英文消息。</param>
    /// <param name="headers">额外的响应头。</param>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <param name="requestId">请求编号。</param>
    /// <returns>拒绝结果。</returns>
    public ServerResult Refusal(int statusCode, string code, string message, IDictionary<string, string> headers, Exception? exception, long requestId)
    {
        ServerResponse response = formatter.Reject(statusCode, code, message, headers);
        if (exception != null)
            logger.LogDebug(exception, "Request {RequestId} was rejected with status {StatusCode}.", requestId, statusCode);
        logger.LogWarning("Request {RequestId} was rejected with status {StatusCode}: {Reason}.", requestId, statusCode, message);
        return new ServerResult(statusCode, response, WebApiServerOutcome.Rejected, code, message, exception);
    }

    /// <summary>未预期的异常：500，只写通用消息；异常详情只进日志（P32-D8）。</summary>
    /// <param name="exception">异常。</param>
    /// <param name="requestId">请求编号。</param>
    /// <returns>500 结果。</returns>
    public ServerResult InternalFailure(Exception exception, long requestId)
    {
        logger.LogError(exception, "Request {RequestId} failed with an unexpected error.", requestId);
        string code = Text(500);
        ServerResponse response = formatter.Reject(500, code, UnexpectedErrorMessage, new Dictionary<string, string>());
        return new ServerResult(500, response, WebApiServerOutcome.HandlerFailed, code, UnexpectedErrorMessage, exception);
    }

    /// <summary>处理超时：504。</summary>
    /// <param name="requestId">请求编号。</param>
    /// <returns>504 结果。</returns>
    public ServerResult TimedOut(long requestId)
    {
        logger.LogWarning("Request {RequestId} timed out.", requestId);
        string code = Text(504);
        ServerResponse response = formatter.Reject(504, code, TimeoutMessage, new Dictionary<string, string>());
        return new ServerResult(504, response, WebApiServerOutcome.TimedOut, code, TimeoutMessage);
    }

    /// <summary>对端在响应产生之前断开：没有响应可写。</summary>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <param name="requestId">请求编号。</param>
    /// <returns>断开结果。</returns>
    public ServerResult Disconnected(Exception? exception, long requestId)
    {
        logger.LogDebug(exception, "Request {RequestId}: the client disconnected before the response was produced.", requestId);
        return new ServerResult(0, null, WebApiServerOutcome.ClientDisconnected, null, null, exception);
    }

    private static string Text(int statusCode) => statusCode.ToString(CultureInfo.InvariantCulture);
}
