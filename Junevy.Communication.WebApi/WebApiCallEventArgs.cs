namespace Junevy.Communication.WebApi;

/// <summary>
/// 客户端调用事件参数的公共部分（仅服务客户端：<see cref="CallId"/> 是客户端内的调用序号）。
/// </summary>
public abstract class WebApiCallEventArgs : EventArgs
{
    /// <summary>保存公共参数。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="callId">调用序号（同一客户端内单调递增，从 1 开始）。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="requestUri">请求的绝对 URI，查询参数已按配置脱敏。</param>
    /// <exception cref="ArgumentNullException">任一引用类型参数为 null。</exception>
    protected WebApiCallEventArgs(string endpointName, long callId, string method, Uri requestUri)
    {
        EndpointName = endpointName ?? throw new ArgumentNullException(nameof(endpointName));
        CallId = callId;
        Method = method ?? throw new ArgumentNullException(nameof(method));
        RequestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
    }

    /// <summary>端点名称。</summary>
    public string EndpointName { get; }

    /// <summary>调用序号：同一客户端内单调递增，从 1 开始。</summary>
    public long CallId { get; }

    /// <summary>HTTP 方法。</summary>
    public string Method { get; }

    /// <summary>请求的绝对 URI，查询参数已按配置脱敏（值为 <c>***</c>）。</summary>
    public Uri RequestUri { get; }
}
