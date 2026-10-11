using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Core.Diagnostics;

/// <summary>
/// 事件的逐订阅者派发：单个订阅者抛出的异常只记录 Error 日志，其余订阅者照常收到事件。
/// 通道、客户端等所有"在自己的执行流上同步触发事件"的类型共用这一份实现。
/// </summary>
public static class EventSubscribers
{
    /// <summary>
    /// 逐个调用事件的订阅者：单个订阅者抛出的异常只记录 Error 日志，其余订阅者照常收到事件。
    /// </summary>
    /// <typeparam name="TArgs">事件参数类型。</typeparam>
    /// <param name="handler">事件的多播委托；为 null 时无操作。</param>
    /// <param name="sender">事件发送者。</param>
    /// <param name="args">事件参数。</param>
    /// <param name="logger">用于记录订阅者异常的日志记录器。</param>
    /// <param name="eventName">事件名称，仅用于日志。</param>
    public static void InvokeEach<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args, ILogger logger, string eventName)
        where TArgs : EventArgs
    {
        if (handler == null)
            return;

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)subscriber)(sender, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A {Event} subscriber threw an exception; the remaining subscribers still receive the event.", eventName);
            }
        }
    }
}
