namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>页签的运行状态（计划第 5.2 节）。各协议把自己的状态事件映射到这里。</summary>
public enum SessionState
{
    NotConnected,
    Connecting,
    Connected,
    Reconnecting,
    Running,
    Stopped,
    Faulted,
}
