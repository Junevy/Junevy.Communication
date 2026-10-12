using System.Buffers;
using System.Text;
using SdkClient = MQTTnet;
using SdkExceptions = MQTTnet.Exceptions;
using SdkFormatter = MQTTnet.Formatter;
using SdkPackets = MQTTnet.Packets;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// 本库消息与 SDK 报文的双向映射。
/// <para>出方向先做校验：主题合法性交给 SDK 的校验器，MQTT 5 属性在 3.1.1 上不受支持也由 SDK 的校验器判定，
/// 本库不另写一份规则（P61-D3、P61-D6）。</para>
/// <para>入方向**复制**正文、关联数据与用户属性（P61-D3）：SDK 的缓冲区可能被复用。</para>
/// </summary>
internal static class MqttSdkMessages
{
    /// <summary>把本库消息映射成 SDK 报文。</summary>
    /// <param name="message">本库消息。</param>
    /// <returns>SDK 报文。</returns>
    public static SdkClient.MqttApplicationMessage ToSdk(MqttMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = new SdkClient.MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload.ToArray())
            .WithQualityOfServiceLevel(MqttSdkQos.ToSdk(message.Qos))
            .WithRetainFlag(message.Retain);

        if (message.ContentType is not null)
        {
            payload.WithContentType(message.ContentType);
        }

        if (message.ResponseTopic is not null)
        {
            payload.WithResponseTopic(message.ResponseTopic);
        }

        if (!message.CorrelationData.IsEmpty)
        {
            payload.WithCorrelationData(message.CorrelationData.ToArray());
        }

        if (message.MessageExpiryInterval is not null)
        {
            payload.WithMessageExpiryInterval(message.MessageExpiryInterval.Value);
        }

        foreach (MqttUserProperty property in message.UserProperties)
        {
            payload.WithUserProperty(property.Name, Encoding.UTF8.GetBytes(property.Value));
        }

        return payload.Build();
    }

    /// <summary>校验一条消息能不能在给定的协议版本上发布；不能时给出原因。</summary>
    /// <param name="message">本库消息。</param>
    /// <param name="version">当前连接使用的协议版本。</param>
    /// <param name="error">不合法时的原因，合法时为 null。</param>
    /// <returns>可以发布时为 true。</returns>
    public static bool TryValidateForPublish(MqttMessage message, MqttProtocolVersion version, out string? error)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!MqttSdkTopics.TryValidatePublishTopic(message.Topic, out error))
        {
            return false;
        }

        if (!MqttSdkQos.IsDefined(message.Qos))
        {
            error = $"the QoS value {(int)message.Qos} is not defined.";
            return false;
        }

        try
        {
            // 这一步负责"3.1.1 不接受 MQTT 5 属性"里的四项（ContentType、ResponseTopic、CorrelationData、UserProperties）。
            SdkClient.MqttApplicationMessageValidator.ThrowIfNotSupported(ToSdk(message), SdkVersion(version));
        }
        catch (Exception exception) when (exception is SdkExceptions.MqttProtocolViolationException or NotSupportedException)
        {
            error = exception.Message;
            return false;
        }

        // MessageExpiryInterval 是唯一一项 SDK 的校验器不当作"5.0 only"的属性（P6-C14，摩擦记录 F12），
        // 但 P61-D3 要求 3.1.1 上一项都不能带，因此在这里补上——只补这一项，其余仍交给 SDK。
        if (version == MqttProtocolVersion.V311 && message.MessageExpiryInterval is not null)
        {
            error = "MessageExpiryInterval is an MQTT 5 property and cannot be used on a 3.1.1 connection.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>把 SDK 报文映射成本库消息。</summary>
    /// <param name="message">SDK 报文。</param>
    /// <returns>与 SDK 缓冲区无关的消息。</returns>
    public static MqttMessage FromSdk(SdkClient.MqttApplicationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new MqttMessage(message.Topic, CopyPayload(message))
        {
            Qos = MqttSdkQos.FromSdk(message.QualityOfServiceLevel),
            Retain = message.Retain,
            ContentType = message.ContentType,
            ResponseTopic = message.ResponseTopic,
            CorrelationData = message.CorrelationData ?? Array.Empty<byte>(),
            MessageExpiryInterval = message.MessageExpiryInterval == 0 ? null : message.MessageExpiryInterval,
            UserProperties = UserProperties(message.UserProperties),
        };
    }

    internal static SdkFormatter.MqttProtocolVersion SdkVersion(MqttProtocolVersion version) =>
        version == MqttProtocolVersion.V500 ? SdkFormatter.MqttProtocolVersion.V500 : SdkFormatter.MqttProtocolVersion.V311;

    private static byte[] CopyPayload(SdkClient.MqttApplicationMessage message)
    {
        // Payload 是 ReadOnlySequence，可能跨多个分段，因此要整体取出来再复制。
        ReadOnlySequence<byte> payload = message.Payload;
        return payload.IsSingleSegment ? payload.First.ToArray() : payload.ToArray();
    }

    private static IReadOnlyList<MqttUserProperty> UserProperties(List<SdkPackets.MqttUserProperty>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return Array.Empty<MqttUserProperty>();
        }

        var mapped = new MqttUserProperty[properties.Count];
        for (int index = 0; index < properties.Count; index++)
        {
            // 用 ValueBuffer 而不是已过时的 Value：后者要经过 MQTTnet.Packets 里的扩展方法，
            // 而那个命名空间里的 MqttUserProperty 与本库的同名，引用它容易读错。
            mapped[index] = new MqttUserProperty(
                properties[index].Name,
                Encoding.UTF8.GetString(properties[index].ValueBuffer.Span));
        }

        return mapped;
    }
}