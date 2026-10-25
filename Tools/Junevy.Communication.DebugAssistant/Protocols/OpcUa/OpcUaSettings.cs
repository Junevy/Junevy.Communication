using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Protocols.OpcUa;

/// <summary>
/// OPC UA 客户端页签的参数（计划第 7.8 节、DA-D11）：端点与应用标识、证书目录、各类超时与重连，以及订阅的默认参数。
/// 浏览结果、读写列表、订阅与通知属于一次会话，不保存。
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
    bool DiscardOldest)
    : SessionSettings;
