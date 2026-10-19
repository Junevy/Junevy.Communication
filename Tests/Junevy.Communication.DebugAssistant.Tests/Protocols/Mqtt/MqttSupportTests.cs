using Junevy.Communication.DebugAssistant.Protocols.Mqtt;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.Mqtt;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Mqtt;

/// <summary>
/// MQTT 页的纯逻辑（计划第 7.7 节）：连接状态的映射、负载与属性的解析与组装、接收列表的显示文字。
/// </summary>
public sealed class MqttSupportTests
{
    [Theory]
    [InlineData(MqttConnectionState.Disconnected, null, SessionState.NotConnected)]
    [InlineData(MqttConnectionState.Disconnected, "", SessionState.NotConnected)]
    [InlineData(MqttConnectionState.Disconnected, "The broker closed the connection.", SessionState.Faulted)]
    [InlineData(MqttConnectionState.Connecting, null, SessionState.Connecting)]
    [InlineData(MqttConnectionState.Connected, null, SessionState.Connected)]
    [InlineData(MqttConnectionState.Reconnecting, "ServerShuttingDown", SessionState.Reconnecting)]
    public void StateMapping_FollowsTheLibrary_AndAFaultKeepsItsReason(MqttConnectionState state, string? reason, SessionState expected) =>
        Assert.Equal(expected, MqttSupport.ToSessionState(state, reason));

    [Fact]
    public void StateNames_AreChinese()
    {
        Assert.Equal(
            new[] { "未连接", "连接中", "已连接", "重连中" },
            new[]
            {
                MqttSupport.StateName(MqttConnectionState.Disconnected),
                MqttSupport.StateName(MqttConnectionState.Connecting),
                MqttSupport.StateName(MqttConnectionState.Connected),
                MqttSupport.StateName(MqttConnectionState.Reconnecting),
            });
    }

    [Fact]
    public void Payload_TextIsUtf8WithoutEscapes_AndHexAcceptsSpacesAndDashes()
    {
        Assert.Equal(new byte[] { 0x43, 0x3A, 0x5C, 0x74 }, MqttSupport.ParsePayload(@"C:\t", asHex: false));
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x10 }, MqttSupport.ParsePayload("hex: 00 FF 10", asHex: true));
        Assert.Equal(new byte[] { 0x0A, 0x0B }, MqttSupport.ParsePayload("0a-0b", asHex: true));
    }

    [Theory]
    [InlineData("0G")]
    [InlineData("ABC")]
    public void MalformedHex_IsAFormatException(string text) =>
        Assert.Throws<FormatException>(() => MqttSupport.ParsePayload(text, asHex: true));

    [Fact]
    public void Expiry_IsOptional_AndMustBeAWholeNumberOfSeconds()
    {
        Assert.Null(MqttSupport.ParseExpiry("   "));
        Assert.Equal((uint?)60, MqttSupport.ParseExpiry(" 60 "));
        Assert.Throws<FormatException>(() => MqttSupport.ParseExpiry("-1"));
        Assert.Throws<FormatException>(() => MqttSupport.ParseExpiry("1.5"));
    }

    [Fact]
    public void Build_CarriesTheMqtt5Properties_AndLeavesEmptyTextUnset()
    {
        MqttMessage message = MqttSupport.Build(
            "plant/line1",
            new byte[] { 1 },
            MqttQos.AtLeastOnce,
            retain: true,
            contentType: "application/json",
            responseTopic: string.Empty,
            correlationData: new byte[] { 0x0A, 0x0B },
            expiry: 60,
            userProperties: [new MqttUserProperty("tenant", "acme")]);

        Assert.Equal(
            (MqttQos.AtLeastOnce, true, "application/json", (string?)null),
            (message.Qos, message.Retain, message.ContentType, message.ResponseTopic));
        Assert.Equal(new byte[] { 0x0A, 0x0B }, message.CorrelationData.ToArray());
        Assert.Equal((uint?)60, message.MessageExpiryInterval);
        Assert.Equal(new[] { new MqttUserProperty("tenant", "acme") }, message.UserProperties);
    }

    [Fact]
    public void DescribeProperties_ListsEachPropertyOfAReceivedMessage()
    {
        MqttMessage message = MqttSupport.Build(
            "t",
            new byte[] { 1 },
            MqttQos.AtMostOnce,
            retain: false,
            contentType: "text/plain",
            responseTopic: "reply/1",
            correlationData: new byte[] { 0xAB },
            expiry: 5,
            userProperties: [new MqttUserProperty("a", "b")]);

        Assert.Equal(
            "ContentType=text/plain; ResponseTopic=reply/1; CorrelationData=AB; Expiry=5s; a=b",
            MqttSupport.DescribeProperties(message));
    }

    [Fact]
    public void DescribeProperties_OfAPlainMessage_IsEmpty() =>
        Assert.Equal(string.Empty, MqttSupport.DescribeProperties(new MqttMessage("t", "x")));

    [Fact]
    public void HexText_IsTwoDigitsPerByte_SeparatedBySpaces() =>
        Assert.Equal("00 FF 0A", MqttSupport.HexText(new byte[] { 0x00, 0xFF, 0x0A }));
}
