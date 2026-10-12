using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using MQTTnet.Protocol;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttSubscriptionTests
{
    [Fact]
    public async Task Subscribe_ReturnsTheQosTheBrokerGranted()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult<MqttQos> result = await client.SubscribeAsync("a/b", MqttQos.AtLeastOnce);

        Assert.True(result.IsSuccess);
        Assert.Equal(MqttQos.AtLeastOnce, result.Data);
        ObservedSubscription subscription = Assert.Single(broker.Subscriptions);
        Assert.Equal("a/b", subscription.Topic);
    }

    [Theory]
    [InlineData(MqttQos.AtMostOnce, 0)]
    [InlineData(MqttQos.AtLeastOnce, 1)]
    [InlineData(MqttQos.ExactlyOnce, 2)]
    public async Task Subscribe_PassesTheRequestedQosThrough(MqttQos requested, int expected)
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult<MqttQos> result = await client.SubscribeAsync("a/b", requested);

        Assert.True(result.IsSuccess);
        Assert.Equal(requested, result.Data);
        Assert.Equal(expected, Assert.Single(broker.Subscriptions).Qos);
    }

    [Fact]
    public async Task Subscribe_RejectedByTheBroker_ReportsRemoteErrorWithTheReasonCode()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).RejectSubscription("a/b");
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, version: MqttProtocolVersion.V500);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        CommResult<MqttQos> result = await client.SubscribeAsync("a/b");

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(143, result.ProtocolErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/#/b")]
    public async Task Subscribe_InvalidFilter_IsRejectedAndNothingIsSent(string filter)
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult<MqttQos> result = await client.SubscribeAsync(filter);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        await Task.Delay(200);
        Assert.Empty(broker.Subscriptions);
    }

    [Fact]
    public async Task Subscribe_UndefinedQos_IsRejected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult<MqttQos> result = await client.SubscribeAsync("a/b", (MqttQos)9);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(broker.Subscriptions);
    }

    [Fact]
    public async Task Subscribe_NullFilter_Throws()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        await Assert.ThrowsAsync<ArgumentNullException>("topicFilter", () => client.SubscribeAsync(null!));
    }

    [Fact]
    public async Task Subscribe_NotConnected_ReportsNotConnected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        CommResult<MqttQos> result = await client.SubscribeAsync("a/b");

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.Empty(broker.Subscriptions);
    }

    [Fact]
    public async Task Unsubscribe_StopsDelivery()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        var collector = new MqttMessageCollector(client);
        await client.ConnectAndAssertAsync();
        await SubscribeAndWaitAsync(client, broker, "a/b");

        await broker.InjectAsync("a/b", "before-unsubscribe");
        await collector.WaitForCountAsync(1);

        Assert.True((await client.UnsubscribeAsync("a/b")).IsSuccess);

        await broker.InjectAsync("a/b", "after-unsubscribe");
        await collector.ExpectSilenceAsync();
    }

    [Fact]
    public async Task Unsubscribe_AnUnknownFilter_Succeeds()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult result = await client.UnsubscribeAsync("never/subscribed");

        Assert.True(result.IsSuccess, result.ToString());
    }

    [Fact]
    public async Task Unsubscribe_NotConnected_ReportsNotConnected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        CommResult result = await client.UnsubscribeAsync("a/b");

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }

    [Fact]
    public async Task Unsubscribe_NullFilter_Throws()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        await Assert.ThrowsAsync<ArgumentNullException>("topicFilter", () => client.UnsubscribeAsync(null!));
    }

    private static async Task SubscribeAndWaitAsync(MqttClient client, TestBroker broker, string filter)
    {
        Assert.True((await client.SubscribeAsync(filter)).IsSuccess);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count > 0);
    }
}