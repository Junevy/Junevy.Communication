using Junevy.Communication.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 服务器的请求事件（内部）：<c>RequestReceived</c> 与 <c>RequestCompleted</c> 的派发。
/// 订阅者逐个调用（<see cref="EventSubscribers"/>）：单个订阅者抛出的异常只记 Error，其余订阅者与响应都不受影响（P32-D10）。
/// </summary>
internal sealed class ServerEvents
{
    private readonly ILogger logger;

    /// <summary>创建事件派发器。</summary>
    /// <param name="logger">用于记录订阅者异常的日志记录器。</param>
    public ServerEvents(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>请求进入服务器（路由之前）。</summary>
    public event EventHandler<WebApiServerRequestEventArgs>? RequestReceived;

    /// <summary>请求结束（每个请求恰好一次）。</summary>
    public event EventHandler<WebApiServerCompletedEventArgs>? RequestCompleted;

    /// <summary>派发 <see cref="RequestReceived"/>。</summary>
    /// <param name="args">事件参数。</param>
    public void RaiseRequestReceived(WebApiServerRequestEventArgs args)
        => EventSubscribers.InvokeEach(RequestReceived, this, args, logger, nameof(RequestReceived));

    /// <summary>派发 <see cref="RequestCompleted"/>。</summary>
    /// <param name="args">事件参数。</param>
    public void RaiseRequestCompleted(WebApiServerCompletedEventArgs args)
        => EventSubscribers.InvokeEach(RequestCompleted, this, args, logger, nameof(RequestCompleted));
}
