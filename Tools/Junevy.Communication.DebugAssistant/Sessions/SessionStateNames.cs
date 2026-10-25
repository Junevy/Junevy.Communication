namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>页签状态与结果错误种类的中文名称（DA-D17：界面用中文，库的错误消息原样显示）。</summary>
public static class SessionStateNames
{
    /// <summary>状态的中文名称。</summary>
    public static string Of(SessionState state) => state switch
    {
        SessionState.NotConnected => "未连接",
        SessionState.Connecting => "连接中",
        SessionState.Connected => "已连接",
        SessionState.Reconnecting => "重连中",
        SessionState.Running => "运行中",
        SessionState.Stopped => "已停止",
        SessionState.Faulted => "故障",
        _ => state.ToString(),
    };
}
