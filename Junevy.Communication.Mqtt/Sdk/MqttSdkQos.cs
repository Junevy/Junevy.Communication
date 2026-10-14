using SdkProtocol = MQTTnet.Protocol;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// 本库的 QoS 与 SDK 的 QoS 之间的换算，两处都要用（P61-D3、P61-D6、P61-D14），因此只写一份。
/// </summary>
internal static class MqttSdkQos
{
    public static SdkProtocol.MqttQualityOfServiceLevel ToSdk(MqttQos qos) => qos switch
    {
        MqttQos.AtLeastOnce => SdkProtocol.MqttQualityOfServiceLevel.AtLeastOnce,
        MqttQos.ExactlyOnce => SdkProtocol.MqttQualityOfServiceLevel.ExactlyOnce,
        _ => SdkProtocol.MqttQualityOfServiceLevel.AtMostOnce,
    };

    public static MqttQos FromSdk(SdkProtocol.MqttQualityOfServiceLevel qos) => qos switch
    {
        SdkProtocol.MqttQualityOfServiceLevel.AtLeastOnce => MqttQos.AtLeastOnce,
        SdkProtocol.MqttQualityOfServiceLevel.ExactlyOnce => MqttQos.ExactlyOnce,
        _ => MqttQos.AtMostOnce,
    };

    /// <summary>QoS 是否为已定义的取值；未定义时按 <c>CommErrorKind.InvalidRequest</c> 处理（P61-D6）。</summary>
    /// <param name="qos">待检查的 QoS。</param>
    /// <returns>已定义时为 true。</returns>
    public static bool IsDefined(MqttQos qos) => Enum.IsDefined(qos);
}