namespace Junevy.Communication.DebugAssistant.Services;

/// <summary>把工作派发回界面线程。WPF 实现位于 Views/Platform；测试使用由测试自己推进的实现。</summary>
public interface IUiDispatcher
{
    /// <summary>异步地在界面线程上执行 <paramref name="action"/>。可以从任何线程调用。</summary>
    void Post(Action action);
}
