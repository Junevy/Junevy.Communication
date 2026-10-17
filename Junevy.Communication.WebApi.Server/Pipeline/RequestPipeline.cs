using System.Globalization;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.WebApi.Server.Auth;
using Junevy.Communication.WebApi.Server.Body;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Dedup;
using Junevy.Communication.WebApi.Server.Formatting;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 请求管线（内部，P32-D8、D9、D10）。阶段依次是：读取正文（408 / 413）→ 解码（400 / 415）→ 认证（401）→ 请求拦截器 → 处理器 → 响应拦截器 → 写出结果的内容。
/// 读取正文之后的阶段都在同一个超时之下（超时 504，读取正文超时 408）；处理器或拦截器不理会取消令牌而继续运行时，响应照发，结果丢弃并记 Warning。
/// 响应拦截器只对读取了正文的请求执行：正文阶段的拒绝（408、413、正文为空等）没有构成完整的请求上下文。
/// 不接触 HttpContext；写出响应与触发事件由 Hosting 层负责。
/// </summary>
internal sealed class RequestPipeline
{
    private const string TimeoutMessage = "The request timed out.";
    private const string UnexpectedErrorMessage = "An unexpected error occurred.";
    private const string AuthenticationMessage = "Authentication failed.";
    private const string WwwAuthenticateHeader = "WWW-Authenticate";
    private const string ReplayedHeader = "Idempotent-Replayed";

    private readonly WebApiServerSettings settings;
    private readonly ResponseFormatter formatter;
    private readonly ILogger logger;
    private readonly ServerAuthenticator? authenticator;
    private readonly IWebApiServerInterceptor[] interceptors;
    private readonly DeduplicationCache? dedup;

    /// <summary>创建管线。</summary>
    /// <param name="settings">已校验的配置。</param>
    /// <param name="formatter">响应格式化器。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="authenticator">认证；不认证时为 null。</param>
    /// <param name="interceptors">拦截器（已拍快照，按注册顺序）。</param>
    /// <param name="dedup">重复下发的去重缓存；去重关闭时为 null。</param>
    public RequestPipeline(WebApiServerSettings settings, ResponseFormatter formatter, ILogger logger, ServerAuthenticator? authenticator, IWebApiServerInterceptor[] interceptors, DeduplicationCache? dedup)
    {
        this.settings = settings;
        this.formatter = formatter;
        this.logger = logger;
        this.authenticator = authenticator;
        this.interceptors = interceptors;
        this.dedup = dedup;
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
            return Refusal(413, "The request body exceeds the configured maximum size.", ex, requestId);
        }
        catch (RequestBodyMalformedException ex)
        {
            return Refusal(400, "The request body could not be read.", ex, requestId);
        }
        catch (OperationCanceledException) when (timeout.IsTimedOut)
        {
            return Refusal(408, "The request body was not received in time.", null, requestId);
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
            return Refusal(400, "The request body could not be read.", ex, requestId);
        }

        var context = new WebApiServerContext(
            serverName, requestId, raw.Method, raw.Path, raw.RouteTemplate, raw.RouteValues,
            raw.Query, raw.Headers, raw.ContentType, body, raw.RemoteEndPoint);
        return await RunWithinTimeoutAsync(route, raw.ContentType, body, context, timeout, clientToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 生成框架或拦截器的拒绝结果（404、405、503 等），与管线内部的拒绝走同一个格式化出口。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="message">英文消息。</param>
    /// <param name="headers">额外的响应头，例如 <c>Allow</c> 或 <c>Retry-After</c>。</param>
    /// <param name="requestId">请求编号（只用于日志）。</param>
    /// <returns>拒绝结果。</returns>
    public ServerResult Reject(int statusCode, string message, IDictionary<string, string> headers, long requestId)
        => Refusal(statusCode, statusCode.ToString(CultureInfo.InvariantCulture), message, headers, null, requestId);

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

    private async Task<ServerResult> RunWithinTimeoutAsync(IRoute route, string? contentType, byte[] body, WebApiServerContext context, TimeoutScope timeout, CancellationToken clientToken)
    {
        Task<ServerResult> invocation = RunStagesAsync(route, contentType, body, context, timeout.Token);
        try
        {
            return await invocation.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsTimedOut)
        {
            ObserveAbandonedInvocation(invocation, context.RequestId);
            string code = 504.ToString(CultureInfo.InvariantCulture);
            ServerResponse response = formatter.Reject(504, code, TimeoutMessage, new Dictionary<string, string>());
            logger.LogWarning("Request {RequestId} timed out.", context.RequestId);
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

    // 阶段的异常转换为 500；取消（超时或对端断开）原样向上传播，由调用方区分处理。
    private async Task<ServerResult> RunStagesAsync(IRoute route, string? contentType, byte[] body, WebApiServerContext context, CancellationToken cancellationToken)
    {
        var run = new InterceptorRun(interceptors, context);
        ServerResult result;
        try
        {
            result = await ProcessAsync(run, route, contentType, body, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = InternalFailure(ex, context.RequestId);
        }

        return await ApplyResponseStageAsync(run, context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ServerResult> ProcessAsync(InterceptorRun run, IRoute route, string? contentType, byte[] body, WebApiServerContext context, CancellationToken cancellationToken)
    {
        object? request = null;
        if (route.RequestType != null)
        {
            BodyReadResult decoded = RequestBodyReader.Read(body, contentType, route.RequestType, settings.Serializer);
            if (!decoded.IsSuccess)
                return Refusal(decoded.StatusCode, decoded.Message!, decoded.Exception, context.RequestId);

            request = decoded.Value;
        }

        ServerResult? refused = await AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        if (refused != null)
            return refused;

        InterceptorRefusal? intercepted = await run.RunRequestStageAsync(cancellationToken).ConfigureAwait(false);
        if (intercepted != null)
            return Refusal(intercepted.StatusCode, intercepted.Code, intercepted.Message, new Dictionary<string, string>(), null, context.RequestId);

        ServerResult? replayed = Deduplicate(context, out DedupTicket? ticket);
        if (replayed != null)
            return replayed;

        ServerResult result;
        try
        {
            result = await route.InvokeAsync(request, context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ticket?.Abandon();
            throw;
        }

        // 只缓存处理器给出的响应（成功与业务失败）；框架拒绝与异常不缓存，允许重试（P32-D11）。
        if (ticket != null)
        {
            if (result.Outcome is WebApiServerOutcome.Succeeded or WebApiServerOutcome.BusinessFailure)
                ticket.Complete(result);
            else
                ticket.Abandon();
        }

        return result;
    }

    // 返回非 null：请求已经有了结果（重放或拒绝），不再执行处理器。返回 null：继续执行，ticket 为本次的登记（没有幂等键或去重关闭时为 null）。
    private ServerResult? Deduplicate(WebApiServerContext context, out DedupTicket? ticket)
    {
        ticket = null;
        if (dedup == null || !context.Headers.TryGetValue(settings.Deduplication.HeaderName, out string? key) || key.Length == 0)
            return null;

        DedupDecision decision = dedup.Begin(key, context.Method, context.Path, context.Body);
        switch (decision.State)
        {
            case DedupState.Proceed:
                ticket = decision.Ticket;
                return null;
            case DedupState.Replay:
                return Replayed(decision.Replayed!);
            case DedupState.InProgress:
                return Refusal(409, "409", "A request with the same idempotency key is still being processed.", new Dictionary<string, string>(), null, context.RequestId);
            case DedupState.BodyMismatch:
                return Refusal(422, "422", "The idempotency key was already used with a different request body.", new Dictionary<string, string>(), null, context.RequestId);
            default:
                var retryAfter = new Dictionary<string, string> { ["Retry-After"] = "1" };
                return Refusal(503, "503", "Too many idempotent requests are in progress; retry later.", retryAfter, null, context.RequestId);
        }
    }

    // 重放第一次的状态码、内容类型与正文，并标记为重放；处理器没有执行。
    private static ServerResult Replayed(ServerResult cached)
    {
        var headers = new Dictionary<string, string>(cached.Response!.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [ReplayedHeader] = "true",
        };
        var response = new ServerResponse(cached.StatusCode, cached.Response.ContentType, cached.Response.Body, headers);
        return new ServerResult(cached.StatusCode, response, WebApiServerOutcome.Replayed, cached.Code, cached.Message);
    }

    private async Task<ServerResult?> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken)
    {
        if (authenticator == null)
            return null;

        WebApiAuthenticationResult result = await authenticator.Authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.IsAuthenticated)
        {
            context.UserName = result.UserName;
            return null;
        }

        logger.LogDebug("Request {RequestId} failed authentication: {Reason}.", context.RequestId, result.FailureMessage);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (authenticator.Challenge != null)
            headers[WwwAuthenticateHeader] = authenticator.Challenge;

        return Refusal(401, "401", AuthenticationMessage, headers, null, context.RequestId);
    }

    private async Task<ServerResult> ApplyResponseStageAsync(InterceptorRun run, WebApiServerContext context, ServerResult result, CancellationToken cancellationToken)
    {
        if (result.Response == null)
            return result;

        try
        {
            await run.RunResponseStageAsync(result.StatusCode, result.Outcome, result.Response.Headers, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return InternalFailure(ex, context.RequestId);
        }
    }

    private ServerResult Refusal(int statusCode, string message, Exception? exception, long requestId)
        => Refusal(statusCode, statusCode.ToString(CultureInfo.InvariantCulture), message, new Dictionary<string, string>(), exception, requestId);

    private ServerResult Refusal(int statusCode, string code, string message, IDictionary<string, string> headers, Exception? exception, long requestId)
    {
        ServerResponse response = formatter.Reject(statusCode, code, message, headers);
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
