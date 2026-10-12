using System.Buffers;
using System.Text;
using SdkClient = MQTTnet;
using SdkPackets = MQTTnet.Packets;
using SdkProtocol = MQTTnet.Protocol;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// SDK 的报文到本库消息的映射。正文、关联数据与用户属性在这里**复制**（P61-D3）：
/// SDK 的缓冲区可能被复用，直接引用会把后续报文的内容暴露给宿主。
/// </summary>
internal static class MqttSdkMessages
{
    /// <summary>把 SDK 的报文映射成本库的消息。</summary>
    /// <param name="message">SDK 的报文。</param>
    /// <returns>与 SDK 缓冲区无关的消息。</returns>
    public static MqttMessage FromSdk(SdkClient.MqttApplicationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new MqttMessage(message.Topic, CopyPayload(message))
        {
            Qos = Qos(message.QualityOfServiceLevel),
            Retain = message.Retain,
            ContentType = message.ContentType,
            ResponseTopic = message.ResponseTopic,
            CorrelationData = message.CorrelationData ?? Array.Empty<byte>(),
            MessageExpiryInterval = message.MessageExpiryInterval == 0 ? null : message.MessageExpiryInterval,
            UserProperties = UserProperties(message.UserProperties),
        };
    }

    private static byte[] CopyPayload(SdkClient.MqttApplicationMessage message)
    {
        // Payload 是 ReadOnlySequence，可能跨多个分段，因此要整体取出来再复制。
        ReadOnlySequence<byte> payload = message.Payload;
        return payload.IsSingleSegment ? payload.First.ToArray() : payload.ToArray();
    }

    private static MqttQos Qos(SdkProtocol.MqttQualityOfServiceLevel qos) => qos switch
    {
        SdkProtocol.MqttQualityOfServiceLevel.AtLeastOnce => MqttQos.AtLeastOnce,
        SdkProtocol.MqttQualityOfServiceLevel.ExactlyOnce => MqttQos.ExactlyOnce,
        _ => MqttQos.AtMostOnce,
    };

    private static IReadOnlyList<MqttUserProperty> UserProperties(List<SdkPackets.MqttUserProperty>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return Array.Empty<MqttUserProperty>();
        }

        var mapped = new MqttUserProperty[properties.Count];
        for (int index = 0; index < properties.Count; index++)
        {
            // 用 ValueBuffer 而不是已过时的 Value：后者还要经过 MQTTnet.Packets 里的扩展方法，
        // 而那个命名空间里的 MqttUserProperty 与本库的同名，引用它容易读错。
        mapped[index] = new MqttUserProperty(
            properties[index].Name,
            Encoding.UTF8.GetString(properties[index].ValueBuffer.Span));
        }

        return mapped;
    }
}