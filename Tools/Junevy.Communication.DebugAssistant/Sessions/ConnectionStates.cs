using Junevy.Communication.Channels;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>库的连接状态与页签状态的映射（计划第 5.2 节）：通道与协议客户端共用。</summary>
internal static class ConnectionStates
{
    public static SessionState ToSessionState(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => SessionState.Connecting,
        ConnectionState.Connected => SessionState.Connected,
        ConnectionState.Reconnecting => SessionState.Reconnecting,
        _ => SessionState.NotConnected,
    };
}
