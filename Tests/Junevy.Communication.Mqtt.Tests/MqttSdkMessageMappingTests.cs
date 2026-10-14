using Junevy.Communication.Mqtt.Sdk;
using MQTTnet;
using MQTTnet.Packets;
using SdkUserProperty = MQTTnet.Packets.MqttUserProperty;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 正文复制（P61-D3）在映射层直接测试：端到端的用例证明不了，因为 SDK 不一定复用缓冲区。
/// </summary>
public sealed class MqttSdkMessageMappingTests
{
    [Fact]
    public void FromSdk_CopiesThePayload()
    {
        byte[] buffer = { 1, 2, 3 };
        var sdk = new MqttApplicationMessageBuilder().WithTopic("a/b").WithPayload(buffer).Build();

        MqttMessage message = MqttSdkMessages.FromSdk(sdk);
        Array.Clear(buffer);

        Assert.Equal(new byte[] { 1, 2, 3 }, message.Payload.ToArray());
    }

    [Fact]
    public void FromSdk_CopiesTheCorrelationData()
    {
        byte[] correlation = { 7, 7 };
        var sdk = new MqttApplicationMessageBuilder()
            .WithTopic("a/b")
            .WithPayload(new byte[] { 1 })
            .WithCorrelationData(correlation)
            .Build();

        MqttMessage message = MqttSdkMessages.FromSdk(sdk);
        Array.Clear(correlation);

        Assert.Equal(new byte[] { 7, 7 }, message.CorrelationData.ToArray());
    }

    [Fact]
    public void FromSdk_CopiesUserProperties()
    {
        var sdk = new MqttApplicationMessageBuilder()
            .WithTopic("a/b")
            .WithPayload(new byte[] { 1 })
            .WithUserProperty("k", System.Text.Encoding.UTF8.GetBytes("v"))
            .Build();

        MqttMessage message = MqttSdkMessages.FromSdk(sdk);
        sdk.UserProperties.Clear();

        Assert.Equal(new[] { new MqttUserProperty("k", "v") }, message.UserProperties);
    }

    [Fact]
    public void FromSdk_MapsDeliveryFields()
    {
        var sdk = new MqttApplicationMessageBuilder()
            .WithTopic("a/b")
            .WithPayload(new byte[] { 1 })
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
            .WithRetainFlag(true)
            .WithContentType("text/plain")
            .WithResponseTopic("a/reply")
            .WithMessageExpiryInterval(30)
            .Build();

        MqttMessage message = MqttSdkMessages.FromSdk(sdk);

        Assert.Equal("a/b", message.Topic);
        Assert.Equal(MqttQos.ExactlyOnce, message.Qos);
        Assert.True(message.Retain);
        Assert.Equal("text/plain", message.ContentType);
        Assert.Equal("a/reply", message.ResponseTopic);
        Assert.Equal(30u, message.MessageExpiryInterval);
    }

    [Fact]
    public void TryValidateForPublish_RejectsAnInvalidTopic()
    {
        var message = new MqttMessage("a/#", "x");

        Assert.False(MqttSdkMessages.TryValidateForPublish(message, MqttProtocolVersion.V311, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryValidateForPublish_AcceptsAPlainMessageOnV311()
    {
        var message = new MqttMessage("a/b", "x");

        Assert.True(MqttSdkMessages.TryValidateForPublish(message, MqttProtocolVersion.V311, out string? error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateForPublish_RejectsANonEmptyContentTypeOnV311()
    {
        var message = new MqttMessage("a/b", "x") { ContentType = "text/plain" };

        Assert.False(MqttSdkMessages.TryValidateForPublish(message, MqttProtocolVersion.V311, out _));
    }

    // 空字符串在报文上与"没有设置"没有区别，SDK 按未设置处理（摩擦记录 F13）。
    [Fact]
    public void TryValidateForPublish_TreatsAnEmptyContentTypeAsUnset()
    {
        var message = new MqttMessage("a/b", "x") { ContentType = string.Empty };

        Assert.True(MqttSdkMessages.TryValidateForPublish(message, MqttProtocolVersion.V311, out _));
    }

    [Fact]
    public void TryValidateForPublish_RejectsTheExpiryIntervalOnV311()
    {
        var message = new MqttMessage("a/b", "x") { MessageExpiryInterval = 30 };

        Assert.False(MqttSdkMessages.TryValidateForPublish(message, MqttProtocolVersion.V311, out string? error));
        Assert.Contains("MessageExpiryInterval", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateForPublish_AcceptsMqtt5PropertiesOnV500()
    {
        var message = new MqttMessage("a/b", "x")
        {
            ContentType = "text/plain",
            ResponseTopic = "a/reply",
            CorrelationData = new byte[] { 1 },
            MessageExpiryInterval = 30,
            UserProperties = new[] { new MqttUserProperty("k", "v") },
        };

        Assert.True(MqttSdkMessages.TryValidateForPublish(message, MqttProtocolVersion.V500, out string? error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateForPublish_RejectsAnUndefinedQos()
    {
        var message = new MqttMessage("a/b", "x") { Qos = (MqttQos)7 };

        Assert.False(MqttSdkMessages.TryValidateForPublish(message, MqttProtocolVersion.V500, out string? error));
        Assert.Contains("not defined", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ToSdk_CarriesEveryMqtt5Property()
    {
        var message = new MqttMessage("a/b", "x")
        {
            Qos = MqttQos.AtLeastOnce,
            Retain = true,
            ContentType = "text/plain",
            ResponseTopic = "a/reply",
            CorrelationData = new byte[] { 1, 2 },
            MessageExpiryInterval = 30,
            UserProperties = new[] { new MqttUserProperty("k", "v") },
        };

        MqttApplicationMessage sdk = MqttSdkMessages.ToSdk(message);

        Assert.Equal("a/b", sdk.Topic);
        Assert.Equal(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, sdk.QualityOfServiceLevel);
        Assert.True(sdk.Retain);
        Assert.Equal("text/plain", sdk.ContentType);
        Assert.Equal("a/reply", sdk.ResponseTopic);
        Assert.Equal(new byte[] { 1, 2 }, sdk.CorrelationData);
        Assert.Equal(30u, sdk.MessageExpiryInterval);
        SdkUserProperty property = Assert.IsType<SdkUserProperty>(Assert.Single(sdk.UserProperties));
        Assert.Equal("k", property.Name);
    }

    [Fact]
    public void ToSdk_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>("message", () => MqttSdkMessages.ToSdk(null!));
    }
}