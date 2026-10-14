using System.Text;

namespace Junevy.Communication.Mqtt.Tests;

public sealed class MqttMessageTests
{
    [Fact]
    public void Constructor_CopiesThePayload()
    {
        byte[] source = { 1, 2, 3 };
        var message = new MqttMessage("a/b", source);

        source[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, message.Payload.ToArray());
    }

    [Fact]
    public void StringConstructor_EncodesUtf8()
    {
        var message = new MqttMessage("a/b", "温度");

        Assert.Equal(Encoding.UTF8.GetBytes("温度"), message.Payload.ToArray());
    }

    [Fact]
    public void TopicOrTextPayloadIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>("topic", () => new MqttMessage(null!, new byte[] { 1 }));
        Assert.Throws<ArgumentNullException>("payload", () => new MqttMessage("a/b", (string)null!));
    }

    [Fact]
    public void Defaults_MatchTheProtocolDefaults()
    {
        var message = new MqttMessage("a/b", Array.Empty<byte>());

        Assert.Equal(MqttQos.AtMostOnce, message.Qos);
        Assert.False(message.Retain);
        Assert.Null(message.ContentType);
        Assert.Null(message.ResponseTopic);
        Assert.Null(message.MessageExpiryInterval);
        Assert.True(message.CorrelationData.IsEmpty);
        Assert.Empty(message.UserProperties);
    }

    [Fact]
    public void CorrelationData_IsCopiedOnSet()
    {
        byte[] source = { 7, 7 };
        var message = new MqttMessage("a/b", Array.Empty<byte>()) { CorrelationData = source };

        source[0] = 9;

        Assert.Equal(new byte[] { 7, 7 }, message.CorrelationData.ToArray());
    }

    [Fact]
    public void UserProperties_AreCopiedOnSet()
    {
        var source = new List<MqttUserProperty> { new MqttUserProperty("k", "v") };
        var message = new MqttMessage("a/b", Array.Empty<byte>()) { UserProperties = source };

        source.Add(new MqttUserProperty("k2", "v2"));
        source.Clear();

        Assert.Equal(new[] { new MqttUserProperty("k", "v") }, message.UserProperties);
    }

    [Fact]
    public void UserProperties_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MqttMessage("a/b", Array.Empty<byte>()) { UserProperties = null! });
    }

    [Fact]
    public void GetPayloadAsString_DecodesUtf8()
    {
        var message = new MqttMessage("a/b", "温度");

        Assert.Equal("温度", message.GetPayloadAsString());
    }

    [Theory]
    [InlineData(MqttQos.AtMostOnce, false, "a/b (QoS 0, 3 bytes)")]
    [InlineData(MqttQos.AtLeastOnce, false, "a/b (QoS 1, 3 bytes)")]
    [InlineData(MqttQos.ExactlyOnce, false, "a/b (QoS 2, 3 bytes)")]
    [InlineData(MqttQos.AtLeastOnce, true, "a/b (QoS 1, retain, 3 bytes)")]
    public void ToString_DescribesDeliveryWithoutThePayload(MqttQos qos, bool retain, string expected)
    {
        var message = new MqttMessage("a/b", new byte[] { 1, 2, 3 }) { Qos = qos, Retain = retain };

        Assert.Equal(expected, message.ToString());
    }

    [Fact]
    public void ToString_NeverContainsThePayload()
    {
        var message = new MqttMessage("secret/topic", "top-secret-payload");

        Assert.DoesNotContain("top-secret-payload", message.ToString(), StringComparison.Ordinal);
    }
}