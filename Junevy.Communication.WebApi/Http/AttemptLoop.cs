using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 一次调用的尝试循环（内部，计划第 11 节）：反复执行单次尝试，直到成功、不可重试或重试用尽。
/// 每次调用恰好触发一次 <c>RequestCompleted</c>；每个已发出的尝试触发一次 <c>ResponseReceived</c>（带是否重试与等待时长），
/// 并向可用性跟踪报告传输层结果。拦截器请求的重放立即再发一次，每次调用最多 1 次，不计入重试次数，也不受方法规则限制。
/// REST 与 SOAP 共用这一份实现，只通过 <see cref="ResponseInterpreter{T}"/> 把响应解释为结果。
/// </summary>
internal sealed class AttemptLoop
{
    private const string RetryAfterHeader = "Retry-After";
    private const int ServerErrorStatus = 500;
    private const string ClosedMessage = "The WebApi client was disposed while the request was in flight.";

    private readonly EndpointIdentity identity;
    private readonly EndpointSettings settings;
    private readonly AttemptRunner runner;
    private readonly RetryPlanner planner;
    private readonly EndpointEvents events;
    private readonly IAvailabilityReporter availability;
    private readonly WebApiRedactor redactor;
    private readonly ILogger logger;
    private long lastCallId;

    /// <summary>创建尝试循环。</summary>
    /// <param name="identity">端点身份。</param>
    /// <param name="settings">端点配置快照。</param>
    /// <param name="exchange">HTTP 交换器。</param>
    /// <param name="chain">拦截器链。</param>
    /// <param name="backoff">覆盖固定间隔的退避策略；null 表示使用 <c>RetryDelay</c>。</param>
    /// <param name="events">请求事件集合。</param>
    /// <param name="availability">接收每次尝试传输层结果的可用性跟踪。</param>
    /// <param name="logger">日志记录器。</param>
    public AttemptLoop(EndpointIdentity identity, EndpointSettings settings, HttpExchange exchange, InterceptorChain chain, IBackoffPolicy? backoff,
                       EndpointEvents events, IAvailabilityReporter availability, ILogger logger)
    {
        this.identity = identity;
        this.settings = settings;
        this.events = events;
        this.availability = availability;
        this.logger = logger;
        redactor = new WebApiRedactor(settings);
        runner = new AttemptRunner(identity, exchange, chain, events, redactor);
        planner = new RetryPlanner(settings, backoff);
    }

    /// <summary>
    /// 执行一次调用。不抛出异常：失败都体现在结果里；返回时 <c>RequestCompleted</c> 的处理器已经执行完毕。
    /// </summary>
    /// <typeparam name="T">结果的数据类型。</typeparam>
    /// <param name="snapshot">请求快照。</param>
    /// <param name="timeout">本次调用每次尝试的超时（毫秒），覆盖配置值；null 使用配置值。</param>
    /// <param name="interpret">协议层解释响应的委托。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    /// <param name="closingToken">端点正在释放的信号；它触发而调用方没有取消时，在途的调用以 <see cref="CommErrorKind.ConnectionClosed"/> 结束。</param>
    /// <returns>调用的最终结果。</returns>
    public async Task<WebApiResult<T>> ExecuteAsync<T>(HttpRequestSnapshot snapshot, int? timeout, ResponseInterpreter<T> interpret,
                                                       CancellationToken cancellationToken, CancellationToken closingToken = default)
    {
        if (!identity.TryResolve(snapshot.RelativeUri, out Uri requestUri))
            return Reject<T>(snapshot.Method, "The request path is not a valid URI.", CommErrorKind.InvalidRequest);

        var call = new CallState(NextCallId(), snapshot, requestUri, redactor.RedactUri(requestUri));
        WebApiResult<T> result = await RunAttemptsAsync(call, timeout ?? settings.Timeout, interpret, cancellationToken, closingToken).ConfigureAwait(false);
        Complete(call.CallId, snapshot.Method, call.RedactedUri, call.SentAttempts, call.Clock.Elapsed, result);
        return result;
    }

    /// <summary>
    /// 以失败结束一个没有进入管线的调用（请求参数非法）：分配调用序号、恰好触发一次 <c>RequestCompleted</c>（<c>Attempts = 0</c>）。
    /// 非法请求没有可解析的 URI，事件里报告端点的基地址。
    /// </summary>
    /// <typeparam name="T">结果的数据类型。</typeparam>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="message">失败信息（英文）。</param>
    /// <param name="kind">失败分类。</param>
    /// <returns>失败结果。</returns>
    public WebApiResult<T> Reject<T>(string method, string message, CommErrorKind kind)
    {
        WebApiResult<T> result = WebApiResult<T>.Fail(message, kind);
        Complete(NextCallId(), method, identity.BaseAddress, 0, TimeSpan.Zero, result);
        return result;
    }

    private long NextCallId() => Interlocked.Increment(ref lastCallId);

    private async Task<WebApiResult<T>> RunAttemptsAsync<T>(CallState call, int timeout, ResponseInterpreter<T> interpret,
                                                            CancellationToken callerToken, CancellationToken closingToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, closingToken);
        while (true)
        {
            call.Attempts++;
            AttemptOutcome<T> attempt = await runner.RunAsync(call, timeout, interpret, linked.Token).ConfigureAwait(false);
            AttemptOutcome<T> outcome = attempt.WithResult(MapDisposal(attempt.Result, callerToken, closingToken));

            // 释放造成的结束是终局：不再重试（否则会对已释放的端点再发起被立即取消的尝试）。
            bool endedByDisposal = !ReferenceEquals(outcome.Result, attempt.Result);
            int? delay = endedByDisposal ? null : PlanNext(call, outcome);
            if (outcome.Sent)
                Report(call, outcome, delay);

            if (delay == null)
                return outcome.Result;
            if (delay > 0 && !await WaitAsync(delay.Value, linked.Token).ConfigureAwait(false))
                return MapDisposal(WebApiResult<T>.Fail("The call was cancelled while waiting to retry.", CommErrorKind.Cancelled), callerToken, closingToken);
        }
    }

    // 释放端点而结束的尝试（取消，但取消来自端点的释放信号而不是调用方）对外一律是 ConnectionClosed：
    // 尝试的 ResponseReceived、最终结果与 RequestCompleted 因此一致。可用性跟踪读的是传输层结果（Cancelled），不会把它算作失败。
    private static WebApiResult<T> MapDisposal<T>(WebApiResult<T> result, CancellationToken callerToken, CancellationToken closingToken)
        => result.ErrorKind == CommErrorKind.Cancelled && closingToken.IsCancellationRequested && !callerToken.IsCancellationRequested
            ? WebApiResult<T>.Fail(ClosedMessage, CommErrorKind.ConnectionClosed, result.StatusCode, null, result.Exception)
            : result;

    // 返回再试之前的等待毫秒数；null 表示结束。重放优先于重试规则。
    private int? PlanNext<T>(CallState call, AttemptOutcome<T> outcome)
    {
        if (outcome.ReplayRequested && !call.Replayed)
        {
            call.Replayed = true;
            return 0;
        }

        if (outcome.NoRetry)
            return null;

        string? retryAfter = null;
        outcome.Exchange?.Response?.Headers.TryGetValue(RetryAfterHeader, out retryAfter);
        WebApiResult<T> result = outcome.Result;
        int? delay = planner.NextDelay(call.RetriesUsed, call.Snapshot.Method, call.Snapshot.IdempotencyKey != null, result.ErrorKind,
                                       result.StatusCode, result.RemoteCode, retryAfter, DateTimeOffset.UtcNow);
        if (delay.HasValue)
            call.RetriesUsed++;

        return delay;
    }

    // 对每个已发出的尝试：先触发 ResponseReceived（此时重试判定已定），再把传输层结果报告给可用性跟踪。
    private void Report<T>(CallState call, AttemptOutcome<T> outcome, int? delay)
    {
        HttpExchangeOutcome exchange = outcome.Exchange!;
        events.RaiseResponse(new WebApiResponseEventArgs(identity.Name, call.CallId, call.Snapshot.Method, call.RedactedUri, call.Attempts,
                                                        exchange.StatusCode, outcome.Result.ErrorKind, outcome.ResponseBody, outcome.Elapsed,
                                                        delay.HasValue, delay ?? 0, outcome.Result.Exception));
        availability.ReportAttempt(exchange.FailureKind, exchange.StatusCode, exchange.Exception);
    }

    private static async Task<bool> WaitAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Complete<T>(long callId, string method, Uri requestUri, int attempts, TimeSpan elapsed, WebApiResult<T> result)
    {
        events.RaiseCompleted(new WebApiCompletedEventArgs(identity.Name, callId, method, requestUri, attempts, result.IsSuccess, result.StatusCode,
                                                           result.RemoteCode, result.ErrorKind, result.ErrorMessage, elapsed));
        if (result.IsSuccess)
            return;

        // 连接中断、超时和服务端错误值得运维注意，其余（4xx、业务失败、取消、参数错误）是正常的失败路径。
        bool serious = result.ErrorKind == CommErrorKind.ConnectionClosed || result.ErrorKind == CommErrorKind.Timeout
                       || result.StatusCode >= ServerErrorStatus;
        logger.Log(serious ? LogLevel.Warning : LogLevel.Debug, "[{Endpoint}#{CallId}] {Method} failed after {Attempts} attempt(s): {Result}",
                   identity.Name, callId, method, attempts, result);
    }
}
