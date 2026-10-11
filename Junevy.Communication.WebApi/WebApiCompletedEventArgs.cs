using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi;

/// <summary>
/// <see cref="IWebApiEndpoint.RequestCompleted"/> 的参数：整个调用的最终结果。每次调用恰好触发一次，宿主只订阅它就能完整记录每次上报。
/// 自定义实现也可以用公开构造函数创建后引发事件。
/// </summary>
public sealed class WebApiCompletedEventArgs : WebApiCallEventArgs
{
    /// <summary>创建参数，保存全部参数。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="callId">调用序号。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="requestUri">最后一次尝试请求的绝对 URI（查询参数已脱敏）。</param>
    /// <param name="attempts">实际发出的尝试次数；没有发出任何尝试为 0。</param>
    /// <param name="isSuccess">调用是否成功。</param>
    /// <param name="statusCode">最终结果的 HTTP 状态码；没有收到响应时为 null。</param>
    /// <param name="remoteCode">对端给出的业务码；没有时为 null。</param>
    /// <param name="errorKind">最终结果的分类；成功为 <see cref="CommErrorKind.None"/>。</param>
    /// <param name="errorMessage">错误信息（英文）；成功时为 null。</param>
    /// <param name="elapsed">整个调用的耗时（含重试的等待）。</param>
    /// <exception cref="ArgumentNullException">任一必需的引用类型参数为 null。</exception>
    public WebApiCompletedEventArgs(string endpointName, long callId, string method, Uri requestUri, int attempts, bool isSuccess,
                                    int? statusCode, string? remoteCode, CommErrorKind errorKind, string? errorMessage, TimeSpan elapsed)
        : base(endpointName, callId, method, requestUri)
    {
        Attempts = attempts;
        IsSuccess = isSuccess;
        StatusCode = statusCode;
        RemoteCode = remoteCode;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
        Elapsed = elapsed;
    }

    /// <summary>实际发出的尝试次数；没有发出任何尝试（校验失败、拦截器中止）为 0。</summary>
    public int Attempts { get; }

    /// <summary>调用是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>最终结果的 HTTP 状态码；没有收到响应时为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>对端给出的业务码；没有时为 null。</summary>
    public string? RemoteCode { get; }

    /// <summary>最终结果的分类；成功为 <see cref="CommErrorKind.None"/>。</summary>
    public CommErrorKind ErrorKind { get; }

    /// <summary>错误信息（英文）；成功时为 null。</summary>
    public string? ErrorMessage { get; }

    /// <summary>整个调用的耗时（含重试的等待）。</summary>
    public TimeSpan Elapsed { get; }
}
