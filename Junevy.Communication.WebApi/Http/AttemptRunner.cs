using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 执行一次尝试（内部，计划第 11 节第 2 步）：从快照重建上下文 → 发送前拦截器 → 同源检查 → <c>RequestSending</c> → HTTP 交换 →
/// 接收后拦截器 → 协议层解释。整个过程在同一个超时作用域内（P3-D11）。重试、重放的判定和 <c>ResponseReceived</c> 不在这里，由 <see cref="AttemptLoop"/> 负责。
/// </summary>
internal sealed class AttemptRunner
{
    private readonly EndpointIdentity identity;
    private readonly HttpExchange exchange;
    private readonly InterceptorChain chain;
    private readonly EndpointEvents events;
    private readonly WebApiRedactor redactor;

    /// <summary>创建执行器。</summary>
    /// <param name="identity">端点身份。</param>
    /// <param name="exchange">HTTP 交换器。</param>
    /// <param name="chain">拦截器链。</param>
    /// <param name="events">请求事件集合。</param>
    /// <param name="redactor">脱敏器。</param>
    public AttemptRunner(EndpointIdentity identity, HttpExchange exchange, InterceptorChain chain, EndpointEvents events, WebApiRedactor redactor)
    {
        this.identity = identity;
        this.exchange = exchange;
        this.chain = chain;
        this.events = events;
        this.redactor = redactor;
    }

    /// <summary>
    /// 执行一次尝试。不抛出异常：失败都体现在结果里。
    /// </summary>
    /// <typeparam name="T">结果的数据类型。</typeparam>
    /// <param name="call">调用状态（<see cref="CallState.Attempts"/> 已加 1）。</param>
    /// <param name="timeout">本次尝试的超时（毫秒）。</param>
    /// <param name="interpret">协议层解释响应的委托。</param>
    /// <param name="userToken">调用方的取消令牌。</param>
    /// <returns>本次尝试的结果。</returns>
    public async Task<AttemptOutcome<T>> RunAsync<T>(CallState call, int timeout, ResponseInterpreter<T> interpret, CancellationToken userToken)
    {
        var clock = Stopwatch.StartNew();
        using TimeoutScope scope = TimeoutScope.Start(timeout, userToken);
        WebApiSendingContext context = CreateContext(call);

        CallFailure? failure;
        try
        {
            failure = await chain.RunSendingAsync(context, scope.Token).ConfigureAwait(false) ?? CheckOrigin(call.RequestUri, context.RequestUri);
        }
        catch (OperationCanceledException ex)
        {
            // 链只在作用域的令牌触发时才传播取消：超时（可重试）或调用方取消。
            (CommErrorKind kind, string message) = HttpStatusClassifier.FromException(ex, scope);
            return AttemptOutcome<T>.NotSent(WebApiResult<T>.Fail(message, kind, null, null, ex), false, clock.Elapsed);
        }

        if (failure != null)
            return AttemptOutcome<T>.NotSent(WebApiResult<T>.Fail(failure.Message, failure.Kind, null, null, failure.Exception), true, clock.Elapsed);

        call.RedactedUri = redactor.RedactUri(context.RequestUri);
        call.SentAttempts++;
        events.RaiseSending(CreateSendingArgs(call, context));
        HttpExchangeOutcome outcome = await exchange.SendAsync(context, scope).ConfigureAwait(false);

        return outcome.Response == null
            ? AttemptOutcome<T>.WasSent(WebApiResult<T>.Fail(outcome.FailureMessage!, outcome.FailureKind, outcome.StatusCode, null, outcome.Exception),
                                        outcome, Array.Empty<byte>(), false, false, clock.Elapsed)
            : await InterpretAsync(call, context, outcome, scope, interpret, clock).ConfigureAwait(false);
    }

    // 每次尝试都从快照重建上下文：正文交给拦截器的是副本，上一次尝试的修改不会累积。
    private WebApiSendingContext CreateContext(CallState call)
    {
        HttpRequestSnapshot snapshot = call.Snapshot;
        return new WebApiSendingContext(identity.Name, call.CallId, call.Attempts, snapshot.Method, call.RequestUri,
                                        snapshot.Headers.ToDictionary(header => header.Key, header => header.Value), (byte[])snapshot.Body.Clone(),
                                        snapshot.ContentType, snapshot.IdempotencyKey, call.Items);
    }

    // 拦截器可以改路径与查询，但 scheme 与 authority 不能变，否则带着认证头的请求可能被发往别的主机（P3-D17）。
    private static CallFailure? CheckOrigin(Uri original, Uri modified)
        => UriOrigin.AreSame(original, modified)
            ? null
            : CallFailure.Create("An interceptor changed the scheme, host or port of the request URI.", CommErrorKind.InvalidRequest);

    private WebApiSendingEventArgs CreateSendingArgs(CallState call, WebApiSendingContext context)
        => new WebApiSendingEventArgs(identity.Name, call.CallId, context.Method, call.RedactedUri, call.Attempts,
                                      redactor.RedactHeaders(context.Headers, context.SensitiveHeaders), context.Body, context.ContentType);

    private async Task<AttemptOutcome<T>> InterpretAsync<T>(CallState call, WebApiSendingContext context, HttpExchangeOutcome outcome,
                                                            TimeoutScope scope, ResponseInterpreter<T> interpret, Stopwatch clock)
    {
        HttpExchangeResponse response = outcome.Response!;
        var received = new WebApiReceivedContext(identity.Name, call.CallId, call.Attempts, context.Method, context.RequestUri, response.StatusCode,
                                                 response.Headers, response.Body, response.ContentType, call.Items);
        CallFailure? failure;
        try
        {
            failure = await chain.RunReceivedAsync(received, scope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            (CommErrorKind kind, string message) = HttpStatusClassifier.FromException(ex, scope);
            return AttemptOutcome<T>.WasSent(WebApiResult<T>.Fail(message, kind, response.StatusCode, null, ex), outcome, received.Body, false, false, clock.Elapsed);
        }

        if (failure != null)
        {
            WebApiResult<T> failed = WebApiResult<T>.Fail(failure.Message, failure.Kind, response.StatusCode, null, failure.Exception);
            return AttemptOutcome<T>.WasSent(failed, outcome, received.Body, false, true, clock.Elapsed);
        }

        WebApiResult<T> result = Interpret(interpret, response.WithBody(received.Body));
        return AttemptOutcome<T>.WasSent(result, outcome, received.Body, received.ReplayRequested, false, clock.Elapsed);
    }

    // 边界：协议层委托应当返回失败结果而不是抛出；万一抛出，按协议违规处理，保证每次尝试都有结果。
    private static WebApiResult<T> Interpret<T>(ResponseInterpreter<T> interpret, HttpExchangeResponse response)
    {
        try
        {
            return interpret(response);
        }
        catch (Exception ex)
        {
            return WebApiResult<T>.Fail($"The response could not be interpreted: {ex.Message}", CommErrorKind.ProtocolViolation, response.StatusCode, null, ex);
        }
    }
}
