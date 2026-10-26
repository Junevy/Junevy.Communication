using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Protocols.OpcUa;

/// <summary>
/// OPC UA 客户端页签的参数（计划第 7.8 节、DA-D11）：端点与应用标识、证书目录、各类超时与重连、订阅的默认参数，
/// 以及登录用的用户名与密码（用户名为空即匿名）。密码按用户的选择（2026-10-26）**明文**写进工作区，与 MQTT、FTP 页不同；
/// 两项放在末尾并带默认值，旧的工作区文件照常加载为匿名。浏览结果、读写列表、订阅与通知属于一次会话，不保存。
/// </summary>
public sealed record OpcUaSettings(
    string? EndpointUrl,
    string ApplicationName,
    string? ApplicationUri,
    string PkiRoot,
    int ConnectTimeout,
    int OperationTimeout,
    int SessionTimeout,
    int KeepAliveInterval,
    int ReconnectInterval,
    int MaxReconnectInterval,
    int PublishingInterval,
    int? SamplingInterval,
    uint QueueSize,
    bool DiscardOldest,
    string? UserName = null,
    string? Password = null)
    : SessionSettings;
