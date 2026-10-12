using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using MQTTnet;
using MQTTnet.Server;
using MQTTnet.Protocol;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttPublishTests
{
    [Theory]
    [InlineData(MqttQos.AtMostOnce, MqttQualityOfServiceLevel.AtMostOnce)]
    [InlineData(MqttQos.AtLeastOnce, MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData(MqttQos.ExactlyOnce, MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Publish_SendsTheTopicPayloadQosAndRetainFlag(MqttQos qos, MqttQualityOfServiceLevel expected)
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("plant/#")).IsSuccess);

        var message = new MqttMessage("plant/line1/temp", "21.5") { Qos = qos, Retain = true };
        CommResult result = await client.PublishAsync(message);

        Assert.True(result.IsSuccess, result.ToString());
        await Polling.WaitUntilAsync(() => broker.Published.Count >= 1);
        MqttApplicationMessage published = Assert.Single(broker.Published);
        Assert.Equal("plant/line1/temp", published.Topic);
        Assert.Equal("21.5", published.ConvertPayloadToString());
        Assert.Equal(expected, published.QualityOfServiceLevel);
        Assert.True(published.Retain);
    }

    [Fact]
    public async Task Publish_ARetainedMessageIsDeliveredToALaterSubscriber()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        var received = new List<MqttMessage>();
        client.MessageReceived += (_, args) =>
        {
            lock (received)
            {
                received.Add(args.Message);
            }
        };

        Assert.True((await client.SubscribeAsync("devices/#")).IsSuccess);
        Assert.True((await client.PublishAsync(new MqttMessage("devices/1/status", "online") { Retain = true })).IsSuccess);

        await Polling.WaitUntilAsync(() =>
        {
            lock (received)
            {
                return received.Count > 0;
            }
        });

        lock (received)
        {
            Assert.Equal("online", received[0].GetPayloadAsString());
        }
    }

    [Fact]
    public async Task Publish_Mqtt5Properties_RoundTripOnV500()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, version: MqttProtocolVersion.V500);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        var received = new List<MqttMessage>();
        client.MessageReceived += (_, args) =>
        {
            lock (received)
            {
                received.Add(args.Message);
            }
        };

        Assert.True((await client.SubscribeAsync("plant/line1/temp")).IsSuccess);

        var message = new MqttMessage("plant/line1/temp", "21.5")
        {
            Qos = MqttQos.AtLeastOnce,
            ContentType = "text/plain",
            ResponseTopic = "plant/line1/reply",
            CorrelationData = new byte[] { 1, 2, 3 },
            MessageExpiryInterval = 60,
            UserProperties = new[] { new MqttUserProperty("unit", "C") },
        };

        Assert.True((await client.PublishAsync(message)).IsSuccess);

        await Polling.WaitUntilAsync(() =>
        {
            lock (received)
            {
                return received.Count > 0;
            }
        });

        lock (received)
        {
            MqttMessage roundTripped = received[0];
            Assert.Equal("21.5", roundTripped.GetPayloadAsString());
            Assert.Equal("text/plain", roundTripped.ContentType);
            Assert.Equal("plant/line1/reply", roundTripped.ResponseTopic);
            Assert.Equal(new byte[] { 1, 2, 3 }, roundTripped.CorrelationData.ToArray());
            Assert.Equal(60u, roundTripped.MessageExpiryInterval);
            Assert.Equal(new[] { new MqttUserProperty("unit", "C") }, roundTripped.UserProperties);
        }
    }

    [Fact]
    public async Task Publish_AnyMqtt5PropertyOnV311_IsRejectedAndNothingIsSent()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        MqttMessage[] messages =
        {
            new MqttMessage("a/b", "x") { ContentType = "text/plain" },
            new MqttMessage("a/b", "x") { ResponseTopic = "a/reply" },
            new MqttMessage("a/b", "x") { CorrelationData = new byte[] { 1 } },
            new MqttMessage("a/b", "x") { MessageExpiryInterval = 60 },
            new MqttMessage("a/b", "x") { UserProperties = new[] { new MqttUserProperty("k", "v") } },
        };

        foreach (MqttMessage message in messages)
        {
            CommResult result = await client.PublishAsync(message);

            Assert.True(
                result.ErrorKind == CommErrorKind.InvalidRequest,
                $"{message.Topic} / ContentType={message.ContentType} / ResponseTopic={message.ResponseTopic} / CorrelationData={message.CorrelationData.Length} / Expiry={message.MessageExpiryInterval} / Properties={message.UserProperties.Count} -> {result}");
        }

        // 不静默丢弃：Broker 一个字节都没收到（P61-D3）。
        await Task.Delay(200);
        Assert.Empty(broker.Published);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/+/b")]
    [InlineData("a/#")]
    public async Task Publish_InvalidTopic_IsRejectedAndNothingIsSent(string topic)
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult result = await client.PublishAsync(new MqttMessage(topic, "x"));

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        await Task.Delay(200);
        Assert.Empty(broker.Published);
    }

    [Fact]
    public async Task Publish_UndefinedQos_IsRejected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult result = await client.PublishAsync(new MqttMessage("a/b", "x") { Qos = (MqttQos)7 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(broker.Published);
    }

    [Fact]
    public async Task Publish_NullMessage_Throws()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        await Assert.ThrowsAsync<ArgumentNullException>("message", () => client.PublishAsync(null!));
    }

    [Fact]
    public async Task Publish_BrokersRefusal_ReportsRemoteErrorWithTheReasonCode()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).RejectNextPublish(MqttPubAckReasonCode.QuotaExceeded);
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, version: MqttProtocolVersion.V500);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        CommResult result = await client.PublishAsync(new MqttMessage("a/b", "x") { Qos = MqttQos.AtLeastOnce });

        Assert.Equal(CommErrorKind.ResourceExhausted, result.ErrorKind);
        Assert.Equal(151, result.ProtocolErrorCode);
    }

    [Fact]
    public async Task Publish_NotConnected_ReportsNotConnected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        CommResult result = await client.PublishAsync(new MqttMessage("a/b", "x"));

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }

    [Fact]
    public async Task Publish_NeverLogsThePayload()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var logger = new Junevy.Communication.Testing.TestLogger();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port), logger);
        await client.ConnectAndAssertAsync();

        await client.PublishAsync(new MqttMessage("a/b", "top-secret-payload"));

        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("top-secret-payload", StringComparison.Ordinal));
    }
}