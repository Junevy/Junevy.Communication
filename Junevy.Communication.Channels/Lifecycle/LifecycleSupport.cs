namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 生命周期组件共用的辅助方法：后台任务的启动与心跳探测来源的校验。
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
}
