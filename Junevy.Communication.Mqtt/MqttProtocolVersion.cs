namespace Junevy.Communication.Mqtt;

/// <summary>
/// 连接的 MQTT 协议版本（P61-D2）。不支持 3.1：现场 Broker 普遍支持的是 3.1.1。
/// </summary>
public enum MqttProtocolVersion
{
    /// <summary>MQTT 3.1.1（默认值）。不接受 MQTT 5 的扩展属性。</summary>
    V311,

    /// <summary>MQTT 5.0，支持扩展属性、原因码与会话过期。</summary>
    V500,
}