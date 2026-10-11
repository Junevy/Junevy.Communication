namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 单次尝试的结果（内部）：协议层解释出的结果，加上尝试循环决定要不要再试、事件里报告什么所需的信息。
/// </summary>
/// <typeparam name="T">结果的数据类型。</typeparam>
internal sealed class AttemptOutcome<T>
{
    private AttemptOutcome(WebApiResult<T> result, HttpExchangeOutcome? exchange, byte[] responseBody, bool replayRequested, bool noRetry, TimeSpan elapsed)
    {
        Result = result;
        Exchange = exchange;
        ResponseBody = responseBody;
        ReplayRequested = replayRequested;
        NoRetry = noRetry;
        Elapsed = elapsed;
    }

    /// <summary>本次尝试的结果。</summary>
    public WebApiResult<T> Result { get; }

    /// <summary>HTTP 交换的结果；请求没有发出（被拦截器中止或超时）时为 null。</summary>
    public HttpExchangeOutcome? Exchange { get; }

    /// <summary>请求是否已经发出（触发过 <c>RequestSending</c>）。</summary>
    public bool Sent => Exchange != null;

    /// <summary>拦截器处理后的响应正文；没有响应时为空数组。</summary>
    public byte[] ResponseBody { get; }

    /// <summary>拦截器是否请求了重放（已排除与 <c>Fail</c> 同时出现的情况）。</summary>
    public bool ReplayRequested { get; }

    /// <summary>结果由拦截器的 <c>Fail</c>、异常或非法的 URI 修改造成，无论分类是什么都不重试。</summary>
    public bool NoRetry { get; }

    /// <summary>本次尝试从开始（含拦截器）到得出结果的耗时。</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>创建"请求没有发出"的结果。</summary>
    /// <param name="result">失败结果。</param>
    /// <param name="noRetry">是否无论分类都不重试。</param>
    /// <param name="elapsed">耗时。</param>
    /// <returns>尝试结果。</returns>
    public static AttemptOutcome<T> NotSent(WebApiResult<T> result, bool noRetry, TimeSpan elapsed)
        => new AttemptOutcome<T>(result, null, Array.Empty<byte>(), false, noRetry, elapsed);

    /// <summary>创建"请求已发出"的结果。</summary>
    /// <param name="result">协议层解释出的结果。</param>
    /// <param name="exchange">HTTP 交换的结果。</param>
    /// <param name="responseBody">拦截器处理后的响应正文。</param>
    /// <param name="replayRequested">拦截器是否请求了重放。</param>
    /// <param name="noRetry">是否无论分类都不重试。</param>
    /// <param name="elapsed">耗时。</param>
    /// <returns>尝试结果。</returns>
    public static AttemptOutcome<T> WasSent(WebApiResult<T> result, HttpExchangeOutcome exchange, byte[] responseBody, bool replayRequested,
                                            bool noRetry, TimeSpan elapsed)
        => new AttemptOutcome<T>(result, exchange, responseBody, replayRequested, noRetry, elapsed);
}
