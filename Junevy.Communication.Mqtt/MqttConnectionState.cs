namespace Junevy.Communication.Mqtt;

/// <summary>
/// 客户端的连接状态（P61-D5）。状态只在 <c>MqttClient</c> 内部的一处变化，并按发生顺序经 <c>StateChanged</c> 通知。
/// </summary>
public enum MqttConnectionState
{
    /// <summary>未连接：初始状态、主动断开之后，以及自动重连关闭时的断线之后。</summary>
    Disconnected,

    /// <summary>首次连接进行中。</summary>
    Connecting,

    /// <summary>已连接。<c>AutoReconnect</c> 为 true 时，宿主看到本状态即表示订阅已经恢复完毕。</summary>
    Connected,

    /// <summary>意外断开后正在后台重连。</summary>
    Reconnecting,
}