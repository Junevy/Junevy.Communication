namespace Junevy.Communication.WebApi;

/// <summary>
/// REST 与 SOAP 客户端共有的契约：名称、对端可用性与四个阶段事件（P3-D3）。
/// 请求事件在调用自身的执行流上同步触发，订阅者抛出的异常只记日志；<see cref="AvailabilityChanged"/> 按顺序由单独的消费者触发。
/// </summary>
public interface IWebApiEndpoint : IDisposable, IAsyncDisposable
{
    /// <summary>端点名称（日志与事件参数中使用）。</summary>
    string Name { get; }

    /// <summary>对端当前的可用性。</summary>
    WebApiAvailability Availability { get; }

    /// <summary>可用性变化时触发（离线缓存补传的触发信号）。</summary>
    event EventHandler<WebApiAvailabilityChangedEventArgs>? AvailabilityChanged;

    /// <summary>每次尝试发出之前触发（拦截器已处理完）。拦截器中止的尝试不会触发。</summary>
    event EventHandler<WebApiSendingEventArgs>? RequestSending;

    /// <summary>每次尝试收到结果之后触发（含网络层失败）；重试判定已经做出。</summary>
    event EventHandler<WebApiResponseEventArgs>? ResponseReceived;

    /// <summary>每次调用结束时恰好触发一次（包括校验失败与拦截器中止）。</summary>
    event EventHandler<WebApiCompletedEventArgs>? RequestCompleted;
}
