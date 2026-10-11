using Junevy.Communication.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 请求事件的集合（内部）：外观类的 <c>event</c> 用 <c>add</c> / <c>remove</c> 转发到这里，<c>sender</c> 是外观对象。
/// 事件在调用自身的执行流上同步触发（P3-D8），逐订阅者隔离异常；每次触发同时写入对应的 Debug 日志。
/// </summary>
internal sealed class EndpointEvents
{
    private readonly object sender;
    private readonly ILogger logger;
    private readonly WebApiLogFormatter formatter;

    /// <summary>创建事件集合。</summary>
    /// <param name="sender">事件的发送者（外观对象）。</param>
    /// <param name="settings">端点配置快照（日志选项）。</param>
    /// <param name="logger">记录订阅者异常与 Debug 日志的记录器。</param>
    public EndpointEvents(object sender, EndpointSettings settings, ILogger logger)
    {
        this.sender = sender;
        this.logger = logger;
        formatter = new WebApiLogFormatter(settings, logger);
    }

    /// <summary>每次尝试发出之前。</summary>
    public event EventHandler<WebApiSendingEventArgs>? RequestSending;

    /// <summary>每次尝试得出结果之后。</summary>
    public event EventHandler<WebApiResponseEventArgs>? ResponseReceived;

    /// <summary>每次调用结束时。</summary>
    public event EventHandler<WebApiCompletedEventArgs>? RequestCompleted;

    /// <summary>可用性变化时（由有序队列的单个消费者触发）。</summary>
    public event EventHandler<WebApiAvailabilityChangedEventArgs>? AvailabilityChanged;

    /// <summary>记录并触发 <see cref="RequestSending"/>。</summary>
    /// <param name="args">事件参数。</param>
    public void RaiseSending(WebApiSendingEventArgs args)
    {
        formatter.LogRequest(args);
        EventSubscribers.InvokeEach(RequestSending, sender, args, logger, nameof(RequestSending));
    }

    /// <summary>记录并触发 <see cref="ResponseReceived"/>。</summary>
    /// <param name="args">事件参数。</param>
    public void RaiseResponse(WebApiResponseEventArgs args)
    {
        formatter.LogResponse(args);
        EventSubscribers.InvokeEach(ResponseReceived, sender, args, logger, nameof(ResponseReceived));
    }

    /// <summary>触发 <see cref="RequestCompleted"/>。</summary>
    /// <param name="args">事件参数。</param>
    public void RaiseCompleted(WebApiCompletedEventArgs args)
        => EventSubscribers.InvokeEach(RequestCompleted, sender, args, logger, nameof(RequestCompleted));

    /// <summary>触发 <see cref="AvailabilityChanged"/>。</summary>
    /// <param name="args">事件参数。</param>
    public void RaiseAvailabilityChanged(WebApiAvailabilityChangedEventArgs args)
        => EventSubscribers.InvokeEach(AvailabilityChanged, sender, args, logger, nameof(AvailabilityChanged));
}
