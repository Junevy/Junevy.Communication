namespace Junevy.Communication.Channels;

/// <summary>
/// 连接状态。状态机见设计文档第 5.1 节。
/// </summary>
public enum ConnectionState
{
    /// <summary>未连接（初始状态，或连接失败、用户断开之后）。</summary>
    Disconnected,

    /// <summary>正在连接（打开链路并执行握手）。</summary>
    Connecting,

    /// <summary>已连接（TCP 为握手完成；串口为端口已打开；UDP 为 socket 已绑定）。</summary>
    Connected,

    /// <summary>连接丢失后正在后台重连。</summary>
    Reconnecting,

    /// <summary>正在断开（优雅关闭中）。</summary>
    Disconnecting,

    /// <summary>已释放，不可再使用。</summary>
    Disposed,
}
