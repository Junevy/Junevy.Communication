using Junevy.Communication.Core.Utils;
using Junevy.Communication.WebApi.Server.Auth;
using Junevy.Communication.WebApi.Server.Body;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Dedup;
using Junevy.Communication.WebApi.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 请求管线（内部，P32-D8、D9、D10）。阶段依次是：读取正文（408 / 413）→ 认证（401）→ 解码（400 / 415）→ 请求拦截器 → 去重 → 处理器 → 响应拦截器。
/// 认证在解码之前：未认证的调用方只得到 401，序列化器不处理未认证的输入；认证器仍能拿到原始正文（签名校验）。
/// 读取正文之后的阶段都在同一个超时之下（超时 504，读取正文超时 408）；处理器或拦截器不理会取消令牌而继续运行时，响应照发，
/// 迟到的结果既不交给响应拦截器也不写出，只记 Warning。响应拦截器只对读取了正文的请求执行：正文阶段的拒绝没有构成完整的请求上下文。
/// 结果的构造在 <see cref="ServerResults"/>；不接触 HttpContext，写出响应与触发事件由 Hosting 层负责。
/// </summary>
internal sealed class RequestPipeline
{
    private const string AuthenticationMessage = "Authentication failed.";
    private const string WwwAuthenticateHeader = "WWW-Authenticate";

    private readonly WebApiServerSettings settings;
    private readonly ServerResults results;
    private readonly ILogger logger;
    private readonly ServerAuthenticator? authenticator;
    private readonly DeduplicationCache? dedup;
    private readonly AbandonedWork abandoned;
    private readonly RequestLog log;

    /// <summary>创建管线。</summary>
    /// <param name="settings">已校验的配置（拦截器取自这里的快照）。</param>
    /// <param name="results">结果构造（与入口共用）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="authenticator">认证；不认证时为 null。</param>
    /// <param name="dedup">重复下发的去重缓存；去重关闭时为 null。</param>
    /// <param name="abandoned">被摘出去的处理器的统计。</param>
    /// <param name="log">每个请求的 Debug 日志。</param>
    public RequestPipeline(WebApiServerSettings settings, ServerResults results, ILogger logger, ServerAuthenticator? authenticator, DeduplicationCache? dedup, AbandonedWork abandoned, RequestLog log)
    {
        this.settings = settings;
        this.results = results;
        this.logger = logger;
        this.authenticator = authenticator;
        this.dedup = dedup;
        this.abandoned = abandoned;
        this.log = log;
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
            return results.RejectBody(413, "The request body exceeds the configured maximum size.", ex, requestId);
        }
        catch (RequestBodyMalformedException ex)
        {
            return results.RejectBody(400, "The request body could not be read.", ex, requestId);
        }
        catch (OperationCanceledException) when (timeout.IsTimedOut)
        {
            return results.RejectBody(408, "The request body was not received in time.", null, requestId);
        }
        catch (OperationCanceledException)
        {
            return results.Disconnected(null, requestId);
        }
        catch (IOException ex)
        {
            return results.Disconnected(ex, requestId);
        }
        catch (Exception ex)
        {
            // 边界：Kestrel 对畸形的正文抛出的其他异常，翻译为 400，详情只进日志。
            return results.RejectBody(400, "The request body could not be read.", ex, requestId);
        }

        log.LogRequest(requestId, raw, body);
        var context = new WebApiServerContext(
            serverName, requestId, raw.Method, raw.Path, raw.RouteTemplate, raw.RouteValues,
            raw.Query, raw.Headers, raw.ContentType, body, raw.RemoteEndPoint);
        return await RunWithinTimeoutAsync(route, context, timeout, clientToken).ConfigureAwait(false);
    }

    // 取消只在令牌确实被取消（超时或对端断开）时按取消处理；处理器自己抛出的取消（例如它内部的 HttpClient 超时）是失败，返回 500。
    private static bool IsFailure(Exception exception, CancellationToken cancellationToken)
        => exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    private async Task<ServerResult> RunWithinTimeoutAsync(IRoute route, WebApiServerContext context, TimeoutScope timeout, CancellationToken clientToken)
    {
        Task<ServerResult> invocation = RunStagesAsync(route, context, timeout.Token);
        try
        {
            return await invocation.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsTimedOut)
        {
            abandoned.Observe(invocation, context.RequestId, "the timeout");
            return results.TimedOut(context.RequestId);
        }
        catch (OperationCanceledException) when (clientToken.IsCancellationRequested)
        {
            abandoned.Observe(invocation, context.RequestId, "the client disconnected");
            return results.Disconnected(null, context.RequestId);
        }
        catch (Exception ex)
        {
            return results.InternalFailure(ex, context.RequestId);
        }
    }

    // 阶段的异常转换为 500；取消（超时或对端断开）原样向上传播，由调用方区分处理。
    private async Task<ServerResult> RunStagesAsync(IRoute route, WebApiServerContext context, CancellationToken cancellationToken)
    {
        var run = new InterceptorRun(settings.Interceptors, context);
        ServerResult result;
        try
        {
            result = await ProcessAsync(run, route, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            result = results.InternalFailure(ex, context.RequestId);
        }

        // 响应已经不再等待（超时或对端断开）：迟到的结果不交给响应拦截器，避免它们看到一条不会写出的响应。
        cancellationToken.ThrowIfCancellationRequested();
        return await ApplyResponseStageAsync(run, context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ServerResult> ProcessAsync(InterceptorRun run, IRoute route, WebApiServerContext context, CancellationToken cancellationToken)
    {
        ServerResult? refused = await AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        if (refused != null)
            return refused;

        object? request = null;
        if (route.RequestType != null)
        {
            BodyReadResult decoded = RequestBodyReader.Read(context.Body, context.ContentType, route.RequestType, settings.Serializer);
            if (!decoded.IsSuccess)
                return results.RejectBody(decoded.StatusCode, decoded.Message!, decoded.Exception, context.RequestId);

            request = decoded.Value;
        }

        WebApiServerRequestingContext.Rejection? rejection = await run.RunRequestStageAsync(cancellationToken).ConfigureAwait(false);
        if (rejection != null)
            return results.Refusal(rejection.StatusCode, rejection.Code, rejection.Message, new Dictionary<string, string>(), null, context.RequestId);

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

        DedupDecision decision = dedup.Begin(key, context.Method, DedupTarget.Of(context.Path, context.Query), context.Body);
        switch (decision.State)
        {
            case DedupState.Proceed:
                ticket = decision.Ticket;
                return null;
            case DedupState.Replay:
                return DedupReplay.Create(decision.Replayed!);
            case DedupState.InProgress:
                return results.Reject(409, "A request with the same idempotency key is still being processed.", new Dictionary<string, string>(), context.RequestId);
            case DedupState.BodyMismatch:
                return results.Reject(422, "The idempotency key was already used with a different request body.", new Dictionary<string, string>(), context.RequestId);
            default:
                return results.Busy("Too many idempotent requests are in progress; retry later.", context.RequestId);
        }
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

        return results.Reject(401, AuthenticationMessage, headers, context.RequestId);
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
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return results.InternalFailure(ex, context.RequestId);
        }
    }
}
