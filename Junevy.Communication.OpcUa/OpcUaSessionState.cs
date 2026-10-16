namespace Junevy.Communication.OpcUa;

/// <summary>
/// OPC UA 会话状态（P62-D11）。由 SDK 的连接状态映射而来：SDK 的 <c>Failover</c> 归入
/// <see cref="Reconnecting"/>，<c>Closing</c> 与 <c>Closed</c> 归入 <see cref="Disconnected"/>。
/// </summary>
public enum OpcUaSessionState
{
    /// <summary>没有会话：未连接、已断开、已释放。</summary>
    Disconnected = 0,

    /// <summary>正在建立会话（首次连接）。</summary>
    Connecting = 1,

    /// <summary>会话可用：读、写、浏览、订阅都可以调用。</summary>
    Connected = 2,

    /// <summary>会话已建立过、当前断开，SDK 正在按退避策略重连（含 <c>Failover</c>）。</summary>
    Reconnecting = 3,
}
