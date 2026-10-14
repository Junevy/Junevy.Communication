namespace Junevy.Communication.Mqtt.Tests.TestSupport;

/// <summary>
/// Broker 看到的一次 CONNECT。CONNECT 报文的字段在这里被摊平成 SDK 无关的类型，
/// 用例因此不必引用 MQTTnet（MQTTnet 只出现在替身与映射层，P6-C5）。
/// </summary>
/// <param name="ClientId">客户端标识。</param>
/// <param name="UserName">用户名。</param>
/// <param name="Password">密码。</param>
/// <param name="CleanSession">Clean Session / Clean Start。</param>
/// <param name="KeepAliveSeconds">保活周期（秒）。</param>
/// <param name="SessionExpiryInterval">会话过期（秒）。</param>
/// <param name="WillDelayInterval">遗嘱延迟（秒）。</param>
public sealed record ObservedConnect(
    string ClientId,
    string? UserName,
    string? Password,
    bool? CleanSession,
    ushort? KeepAliveSeconds,
    uint SessionExpiryInterval,
    uint WillDelayInterval);

/// <summary>Broker 处理过的一次 SUBSCRIBE。</summary>
/// <param name="ClientId">客户端标识。</param>
/// <param name="Topic">主题过滤器。</param>
/// <param name="Qos">请求的 QoS。</param>
public sealed record ObservedSubscription(string ClientId, string Topic, int Qos);