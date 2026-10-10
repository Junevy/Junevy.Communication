using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 生命周期组件共用的辅助方法：后台任务的启动与事件的逐订阅者派发。
/// </summary>
internal static class LifecycleSupport
{
    /// <summary>
    /// 在线程池上启动后台工作，且不继承调用方的 <see cref="AsyncLocal{T}"/> 值。
    /// 派发上下文标记（<c>DispatchScope</c>）必须只属于派发任务自己的执行流，不能随后台工作流入。
    /// </summary>
    /// <param name="work">后台工作；其异常必须在内部处理，否则成为未观察的任务异常。</param>
    /// <returns>后台任务。</returns>
    public static Task StartDetached(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    /// <summary>
    /// 校验心跳探测的来源互斥（第 0 节、设计 5.6）：<see cref="ChannelComponents.HealthProbe"/> 与 <see cref="ChannelComponents.HealthProbeFactory"/> 只能设置其一。
    /// 与心跳是否启用无关，同时设置即为配置错误。
    /// </summary>
    /// <param name="components">代码级覆盖；可为 null。</param>
    /// <exception cref="ArgumentException">两者同时设置。</exception>
    public static void RequireSingleHealthProbe(ChannelComponents? components)
    {
        if (components?.HealthProbe != null && components.HealthProbeFactory != null)
            throw new ArgumentException("ChannelComponents.HealthProbe and ChannelComponents.HealthProbeFactory are mutually exclusive; set only one of them.",
                                        nameof(components));
    }

    /// <summary>
    /// 逐个调用事件的订阅者：单个订阅者抛出的异常只记录 Error 日志，其余订阅者照常收到事件（计划 9.2 的"每个订阅者单独 try/catch"）。
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
