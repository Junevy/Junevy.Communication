namespace Junevy.Communication.Mqtt;

/// <summary>
/// MQTT 质量-of-服务级别。取值与协议中的 QoS 一致，因此可以直接与线上的值比较。
/// </summary>
public enum MqttQos
{
    /// <summary>最多一次：写出即视为成功，不等任何确认。</summary>
    AtMostOnce = 0,

    /// <summary>至少一次：收到 PUBACK 才算成功。</summary>
    AtLeastOnce = 1,

    /// <summary>恰好一次：收到 PUBCOMP 才算成功。</summary>
    ExactlyOnce = 2,
}