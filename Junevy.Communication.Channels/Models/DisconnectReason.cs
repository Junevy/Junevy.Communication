namespace Junevy.Communication.Channels;

/// <summary>
/// 连接断开或进入重连的原因（出现在 <see cref="ConnectionStateChangedEventArgs.Reason"/> 中）。
/// </summary>
public enum DisconnectReason
{
    /// <summary>无原因（非断开类的状态变化）。</summary>
    None,

    /// <summary>用户调用了 DisconnectAsync。</summary>
    UserRequested,

    /// <summary>对端关闭了连接。</summary>
    RemoteClosed,

    /// <summary>发送失败（超时、I/O 错误或用户取消正在写出的帧，见设计文档 D10）。</summary>
    SendFailed,

    /// <summary>请求超时，且 Sequential 模式下配置为断开重建。</summary>
    RequestTimeout,

    /// <summary>应用层心跳连续失败。</summary>
    HeartbeatFailed,

    /// <summary>空闲超时：一段时间内没有任何入站数据。</summary>
    IdleTimeout,

    /// <summary>缓冲区中的未成帧数据超过 PartialFrameTimeout。</summary>
    PartialFrameTimeout,

    /// <summary>协议违规（分帧异常、握手积压溢出等）。</summary>
    ProtocolViolation,

    /// <summary>TLS 证书校验失败或登录失败。</summary>
    AuthenticationFailed,

    /// <summary>重连次数耗尽。</summary>
    ReconnectExhausted,

    /// <summary>其他错误。</summary>
    Error,

    /// <summary>通道被释放。</summary>
    Disposed,
}
