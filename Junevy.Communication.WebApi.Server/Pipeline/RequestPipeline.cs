using System.Globalization;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.WebApi.Server.Body;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Formatting;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 请求管线（内部，P32-D8）：路由已经匹配之后的阶段，依次是读取正文（408 / 413）、解码（400 / 415）、处理器（带超时，504）。
/// 超时由本类自己计时（<see cref="TimeoutScope"/>）：处理器不理会取消令牌而继续运行时，504 已经写出，之后的结果丢弃并记 Warning。
/// 不接触 HttpContext；写出响应与触发事件由 Hosting 层负责。
/// </summary>
internal sealed class RequestPipeline
{
    private const string TimeoutMessage = "The request timed out.";
    private const string UnexpectedErrorMessage = "An unexpected error occurred.";

    private readonly WebApiServerSettings settings;
    private readonly ResponseFormatter formatter;
    private readonly ILogger logger;

    /// <summary>创建管线。</summary>
    /// <param name="settings">已校验的配置。</param>
    /// <param name="formatter">响应格式化器。</param>
    /// <param name="logger">日志记录器。</param>
    public RequestPipeline(WebApiServerSettings settings, ResponseFormatter formatter, ILogger logger)
    {
        this.settings = settings;
        this.formatter = formatter;
        this.logger = logger;
    }

    /// <summary>
    /// 执行一条已匹配路由的请求。除调用方的取消（对端断开）外不抛出异常：所有失败都转换为 <see cref="ServerResult"/>。
    /// </summary>
    /// <param name="raw">请求快照。</param>
    /// <param name="route">命中的路由。</param>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="requestId">请求编号。</param>
    /// <param name="clientToken">对端断开时触发的取消令牌。</param>
    /// <returns>处理结果。</returns>
    public async Task<ServerResult> ExecuteAsync(RawRequest raw, IRoute route, string serverName, long requestId, CancellationToken clientToken)
    {
        using TimeoutScope timeout = TimeoutScope.Start(settings.RequestTimeout, clientToken);

        byte[] body;
        try
        {
            body = await raw.ReadBodyAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (RequestBodyTooLargeException ex)
        {
            return Refuse(413, "The request body exceeds the configured maximum size.", ex, requestId);
        }
        catch (RequestBodyMalformedException ex)
        {
            return Refuse(400, "The request body could not be read.", ex, requestId);
        }
        catch (OperationCanceledException) when (timeout.IsTimedOut)
        {
            return Refuse(408, "The request body was not received in time.", null, requestId);
        }
        catch (OperationCanceledException)
        {
            return Disconnected(null, requestId);
        }
        catch (IOException ex)
        {
            return Disconnected(ex, requestId);
        }
        catch (Exception ex)
        {
            // 边界：Kestrel 对畸形的正文抛出的其他异常，翻译为 400，详情只进日志。
            return Refuse(400, "The request body could not be read.", ex, requestId);
        }

        object? request = null;
        if (route.RequestType != null)
        {
            BodyReadResult decoded = RequestBodyReader.Read(body, raw.ContentType, route.RequestType, settings.Serializer);
            if (!decoded.IsSuccess)
                return Refuse(decoded.StatusCode, decoded.Message!, decoded.Exception, requestId);

            request = decoded.Value;
        }

        var context = new WebApiServerContext(
            serverName, requestId, raw.Method, raw.Path, raw.RouteTemplate, raw.RouteValues,
            raw.Query, raw.Headers, raw.ContentType, body, raw.RemoteEndPoint);
        return await InvokeHandlerAsync(route, request, context, timeout, clientToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 生成框架或拦截器的拒绝结果（404、405、503、413 等），与管线内部的拒绝走同一个格式化出口。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="message">英文消息。</param>
    /// <param name="headers">额外的响应头，例如 <c>Allow</c> 或 <c>Retry-After</c>。</param>
    /// <param name="requestId">请求编号（只用于日志）。</param>
    /// <returns>拒绝结果。</returns>
    public ServerResult Reject(int statusCode, string message, IDictionary<string, string> headers, long requestId)
    {
        string code = statusCode.ToString(CultureInfo.InvariantCulture);
        ServerResponse response = formatter.Reject(statusCode, code, message, headers);
        logger.LogWarning("Request {RequestId} was rejected with status {StatusCode}: {Reason}.", requestId, statusCode, message);
        return new ServerResult(statusCode, response, WebApiServerOutcome.Rejected, code, message);
    }

    private ServerResult Refuse(int statusCode, string message, Exception? exception, long requestId)
    {
        string code = statusCode.ToString(CultureInfo.InvariantCulture);
        ServerResponse response = formatter.Reject(statusCode, code, message, new Dictionary<string, string>());
        if (exception != null)
            logger.LogDebug(exception, "Request {RequestId} was rejected with status {StatusCode}.", requestId, statusCode);
        logger.LogWarning("Request {RequestId} was rejected with status {StatusCode}: {Reason}.", requestId, statusCode, message);
        return new ServerResult(statusCode, response, WebApiServerOutcome.Rejected, code, message, exception);
    }

    private ServerResult Disconnected(Exception? exception, long requestId)
    {
        logger.LogDebug(exception, "Request {RequestId}: the client disconnected before the response was produced.", requestId);
        return new ServerResult(0, null, WebApiServerOutcome.ClientDisconnected, null, null, exception);
    }

    private async Task<ServerResult> InvokeHandlerAsync(IRoute route, object? request, WebApiServerContext context, TimeoutScope timeout, CancellationToken clientToken)
    {
        Task<ServerResult> invocation = route.InvokeAsync(request, context, timeout.Token);
        try
        {
            return await invocation.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsTimedOut)
        {
            ObserveAbandonedInvocation(invocation, context.RequestId);
            string code = 504.ToString(CultureInfo.InvariantCulture);
            ServerResponse response = formatter.Reject(504, code, TimeoutMessage, new Dictionary<string, string>());
            return new ServerResult(504, response, WebApiServerOutcome.TimedOut, code, TimeoutMessage);
        }
        catch (OperationCanceledException) when (clientToken.IsCancellationRequested)
        {
            return Disconnected(null, context.RequestId);
        }
        catch (Exception ex)
        {
            return InternalFailure(ex, context.RequestId);
        }
    }

    /// <summary>
    /// 处理器、拦截器或框架的未预期异常：返回 500，只写通用消息；异常详情只进日志（P32-D8）。
    /// </summary>
    /// <param name="exception">异常。</param>
    /// <param name="requestId">请求编号。</param>
    /// <returns>500 结果。</returns>
    public ServerResult InternalFailure(Exception exception, long requestId)
    {
        logger.LogError(exception, "Request {RequestId} failed with an unexpected error.", requestId);
        string code = 500.ToString(CultureInfo.InvariantCulture);
        ServerResponse response = formatter.Reject(500, code, UnexpectedErrorMessage, new Dictionary<string, string>());
        return new ServerResult(500, response, WebApiServerOutcome.HandlerFailed, code, UnexpectedErrorMessage, exception);
    }

    // 超时之后处理器仍在运行：响应已经发出，它的结果丢弃；任务的异常在这里被观察，避免成为未观察的任务异常。
    private void ObserveAbandonedInvocation(Task<ServerResult> invocation, long requestId)
    {
        if (!invocation.IsCompleted)
            logger.LogWarning("The handler for request {RequestId} is still running after the timeout; its result will be discarded.", requestId);

        _ = invocation.ContinueWith(
            task => logger.LogDebug(task.Exception, "The handler for request {RequestId} finished after the timeout; its result was discarded.", requestId),
            TaskContinuationOptions.ExecuteSynchronously);
    }
}
