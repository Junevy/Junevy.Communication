using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using MQTTnet.Protocol;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 遗嘱消息（P61-D14）：只在异常断开时由 Broker 发布；正常断开与释放发的是 DISCONNECT，Broker 丢弃遗嘱。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class MqttWillTests
{
    [Fact]
    public async Task Will_AbnormalDisconnect_IsPublishedToSubscribers()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "will-owner");
        options.WillTopic = "devices/1/status";
        options.WillPayload = "offline";
        options.WillQos = MqttQos.AtLeastOnce;
        options.WillRetain = true;
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        // 另一个客户端订阅遗嘱主题。
        using var watcher = MqttClient.Create(MqttTestOptions.Valid(broker.Port, "will-watcher"));
        var collector = new MqttMessageCollector(watcher);
        await watcher.ConnectAndAssertAsync();
        Assert.True((await watcher.SubscribeAsync("devices/1/status", MqttQos.AtLeastOnce)).IsSuccess);

        // Broker 以"带遗嘱断开"的原因踢掉被测客户端。
        await broker.DisconnectClientAsync("will-owner");
        await collector.WaitForCountAsync(1);

        MqttMessage will = collector.Messages[0];
        Assert.Equal("devices/1/status", will.Topic);
        Assert.Equal("offline", will.GetPayloadAsString());
        Assert.Equal(MqttQos.AtLeastOnce, will.Qos);

        // 保留标志只在"因新订阅而补发保留消息"时为 true；实时投递一律是 false（MQTT 3.1.1 §3.3.1.3）。
        // 保留本身由下面那条用例验证。
        Assert.False(will.Retain);
    }

    [Fact]
    public async Task Will_Retained_IsRedeliveredToLaterSubscribers()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "will-owner");
        options.WillTopic = "devices/3/status";
        options.WillPayload = "offline";
        options.WillRetain = true;
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();
        await broker.DisconnectClientAsync("will-owner");
        await Polling.WaitUntilAsync(() => broker.Connects.Count == 1);

        using var latecomer = MqttClient.Create(MqttTestOptions.Valid(broker.Port, "latecomer"));
        var collector = new MqttMessageCollector(latecomer);
        await latecomer.ConnectAndAssertAsync();
        Assert.True((await latecomer.SubscribeAsync("devices/3/status")).IsSuccess);

        await collector.WaitForCountAsync(1);
        Assert.Equal("offline", collector.Messages[0].GetPayloadAsString());
        Assert.True(collector.Messages[0].Retain);
    }

    [Fact]
    public async Task Will_NormalDisconnect_IsNotPublished()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "will-owner");
        options.WillTopic = "devices/2/status";
        options.WillPayload = "offline";
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        using var watcher = MqttClient.Create(MqttTestOptions.Valid(broker.Port, "will-watcher"));
        var collector = new MqttMessageCollector(watcher);
        await watcher.ConnectAndAssertAsync();
        Assert.True((await watcher.SubscribeAsync("devices/2/status")).IsSuccess);

        await client.DisconnectAsync();

        // MQTT 的规定：收到 DISCONNECT 就不发遗嘱。
        await collector.ExpectSilenceAsync(500);
    }

    [Fact]
    public async Task Will_NotConfigured_PublishesNothingOnAbnormalDisconnect()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port, "no-will"));

        using var watcher = MqttClient.Create(MqttTestOptions.Valid(broker.Port, "will-watcher"));
        var collector = new MqttMessageCollector(watcher);
        await watcher.ConnectAndAssertAsync();
        Assert.True((await watcher.SubscribeAsync("#")).IsSuccess);
        await client.ConnectAndAssertAsync();

        await broker.DisconnectClientAsync("no-will");

        await collector.ExpectSilenceAsync(500);
    }
}