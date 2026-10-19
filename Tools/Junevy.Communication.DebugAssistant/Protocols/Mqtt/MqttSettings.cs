using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Mqtt;

namespace Junevy.Communication.DebugAssistant.Protocols.Mqtt;

/// <summary>
/// MQTT 客户端页签的参数（计划第 7.7 节、DA-D11）：连接选项、TLS、重连与遗嘱。密码不保存；订阅与发布的内容也不保存，
/// 它们属于一次会话。
/// </summary>
public sealed record MqttSettings(
    string Host,
    int Port,
    string? ClientId,
    string? UserName,
    MqttProtocolVersion ProtocolVersion,
    bool CleanSession,
    uint SessionExpiryInterval,
    int KeepAliveSeconds,
    int TimeoutMilliseconds,
    bool UseTls,
    bool AllowUntrustedCertificates,
    bool AutoReconnect,
    int ReconnectInterval,
    int MaxReconnectInterval,
    string? WillTopic,
    string? WillPayload,
    MqttQos WillQos,
    bool WillRetain)
    : SessionSettings;
