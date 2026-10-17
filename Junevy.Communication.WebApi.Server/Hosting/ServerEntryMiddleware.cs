using System.Diagnostics;
using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Logging;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 请求的入口与出口（内部）：最外层的中间件。入口触发 RequestReceived 并计入在途数；出口决定最终结果、写出响应、触发 RequestCompleted。
/// 每个请求恰好触发一次 RequestCompleted（Q26）。限流拒绝、框架的 404 与 405、管线的结果都在出口汇合到同一个写出路径。
/// </summary>
internal sealed class ServerEntryMiddleware
{
    private const string BusyMessage = "The server is busy; retry later.";
    private const string UnhandledMessage = "The request was not handled.";

    private readonly string serverName;
    private readonly ServerEvents events;
    private readonly RequestPipeline pipeline;
    private readonly ServerResults results;
    private readonly InFlightCounter inFlight;
    private readonly RequestLog log;
    private readonly ILogger logger;
    private long lastRequestId;

    /// <summary>创建入口与出口中间件。</summary>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="events">事件派发器。</param>
    /// <param name="pipeline">请求管线。</param>
    /// <param name="results">结果构造（与管线共用）。</param>
    /// <param name="inFlight">在途请求计数。</param>
    /// <param name="log">每个请求的 Debug 日志。</param>
    /// <param name="logger">日志记录器。</param>
    public ServerEntryMiddleware(string serverName, ServerEvents events, RequestPipeline pipeline, ServerResults results, InFlightCounter inFlight, RequestLog log, ILogger logger)
    {
        this.serverName = serverName;
        this.events = events;
        this.pipeline = pipeline;
        this.results = results;
        this.inFlight = inFlight;
        this.log = log;
        this.logger = logger;
    }

    /// <summary>中间件入口（<c>app.Use</c> 的签名）。</summary>
    /// <param name="context">当前的 HTTP 上下文。</param>
    /// <param name="next">后续中间件。</param>
    /// <returns>请求处理完成的任务。</returns>
    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        long requestId = Interlocked.Increment(ref lastRequestId);
        context.Items[ServerKeys.RequestId] = requestId;
        string method = context.Request.Method.ToUpperInvariant();
        string path = context.Request.Path.Value ?? "/";
        var clock = Stopwatch.StartNew();

        events.RaiseRequestReceived(new WebApiServerRequestEventArgs(serverName, requestId, method, path, HttpRequestAdapter.RemoteEndPointOf(context)));
        inFlight.Enter();
        ServerResult? result = null;
        try
        {
            result = await ProcessAsync(context, next, requestId).ConfigureAwait(false);
        }
        finally
        {
            inFlight.Exit();
            RaiseCompleted(context, requestId, method, path, clock.Elapsed, result ?? new ServerResult(0, null, WebApiServerOutcome.HandlerFailed));
        }
    }

    private async Task<ServerResult> ProcessAsync(HttpContext context, Func<Task> next, long requestId)
    {
        ServerResult result;
        try
        {
            await next().ConfigureAwait(false);
            result = ResolveResult(context, requestId);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            result = new ServerResult(0, null, WebApiServerOutcome.ClientDisconnected);
        }
        catch (Exception ex)
        {
            // 边界：异常发生在管线之外（路由或框架本身），统一返回 500，详情只进日志。
            result = results.InternalFailure(ex, requestId);
        }

        return await WriteAsync(context, result, requestId).ConfigureAwait(false);
    }

    // 结果的来源依次是：管线写入的结果、并发限流的拒绝、框架的 404 与 405。
    private ServerResult ResolveResult(HttpContext context, long requestId)
    {
        if (context.Items.TryGetValue(ServerKeys.Result, out object? produced) && produced is ServerResult routed)
            return routed;

        if (context.Items.ContainsKey(ServerKeys.LimiterRejected))
            return results.Busy(BusyMessage, requestId);

        int status = context.Response.StatusCode;
        if (status < 400)
            return results.InternalFailure(new InvalidOperationException(UnhandledMessage), requestId);

        // 405 的 Allow 头已由框架写在响应上，写出时不清除已有的头，因此这里不需要复制。
        return results.Reject(status, ReasonPhrases.Of(status), new Dictionary<string, string>(), requestId);
    }

    private async Task<ServerResult> WriteAsync(HttpContext context, ServerResult result, long requestId)
    {
        if (result.Response == null)
            return result;

        try
        {
            await HttpResponseWriter.WriteAsync(context.Response, result.Response, context.RequestAborted).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            logger.LogDebug(ex, "Request {RequestId}: the client disconnected before the response was written.", requestId);
            return new ServerResult(0, null, WebApiServerOutcome.ClientDisconnected, result.Code, result.Message, ex);
        }
    }

    private void RaiseCompleted(HttpContext context, long requestId, string method, string path, TimeSpan elapsed, ServerResult result)
    {
        string? routeTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        events.RaiseRequestCompleted(new WebApiServerCompletedEventArgs(
            serverName, requestId, method, path, routeTemplate, result.StatusCode, result.Outcome,
            result.Code, result.Message, result.Exception, elapsed));
        log.LogCompleted(requestId, method, routeTemplate ?? path, result, elapsed);
    }
}
