namespace Junevy.Communication.Core.Results;

/// <summary>
/// 通道族通用的错误分类（粗粒度，供宿主统一告警与重试决策）。
/// 数值 0–7 与 <c>ModbusErrorKind</c> 一一对应（<c>RemoteError</c> 对应 Modbus 的 <c>ModbusException</c>），未来 Modbus 迁移时可直接映射；8–11 为通道族新增。
/// </summary>
public enum CommErrorKind
{
    /// <summary>成功（未失败）。</summary>
    None = 0,

    /// <summary>未分类失败（兼容默认值）。</summary>
    Unspecified = 1,

    /// <summary>本地请求校验失败（参数错误、编程错误）。</summary>
    InvalidRequest = 2,

    /// <summary>操作过程中连接断开或发送失败。</summary>
    ConnectionClosed = 3,

    /// <summary>超时（连接、读、写或请求应答）。</summary>
    Timeout = 4,

    /// <summary>帧格式、长度、校验、关联键不符或反序列化失败等协议违规。</summary>
    ProtocolViolation = 5,

    /// <summary>对端返回的协议级错误（Modbus 异常码、MC 结束码、S7 错误码、HTTP 4xx/5xx 等）。</summary>
    RemoteError = 6,

    /// <summary>调用方取消操作。</summary>
    Cancelled = 7,

    /// <summary>操作开始时尚未连接。</summary>
    NotConnected = 8,

    /// <summary>TLS 证书校验失败或登录失败。</summary>
    AuthenticationFailed = 9,

    /// <summary>资源耗尽（会话数已满、队列已满）。</summary>
    ResourceExhausted = 10,

    /// <summary>传输或协议不支持该操作。</summary>
    NotSupported = 11,
}
