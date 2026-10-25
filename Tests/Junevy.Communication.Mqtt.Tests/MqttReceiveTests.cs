using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttReceiveTests
{
    [Fact]
    public async Task Receive_AnInjectedMessageRaisesMessageReceived()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        var collector = new MqttMessageCollector(client);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("plant/#", MqttQos.AtLeastOnce)).IsSuccess);

        await broker.InjectAsync("plant/line1/temp", "21.5", qos: MqttQualityOfServiceLevel.AtLeastOnce);
        await collector.WaitForCountAsync(1);

        MqttMessage received = collector.Messages[0];
        Assert.Equal("plant/line1/temp", received.Topic);
        Assert.Equal("21.5", received.GetPayloadAsString());
        Assert.Equal(MqttQos.AtLeastOnce, received.Qos);
        Assert.False(received.Retain);
    }

    [Fact]
    public async Task Receive_WildcardFilterGetsEveryMatchingTopic()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        var collector = new MqttMessageCollector(client);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("plant/+/temp")).IsSuccess);

        await broker.InjectAsync("plant/line1/temp", "a");
        await broker.InjectAsync("plant/line2/temp", "b");
        await collector.WaitForCountAsync(2);

        Assert.Equal(new[] { "plant/line1/temp", "plant/line2/temp" }, collector.Messages.Select(message => message.Topic));
    }

    [Fact]
    public async Task Receive_MessagesAreDeliveredInArrivalOrder()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        var collector = new MqttMessageCollector(client);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("seq/#", MqttQos.AtLeastOnce)).IsSuccess);

        for (int index = 0; index < 10; index++)
        {
            await broker.InjectAsync("seq/n", index.ToString(), qos: MqttQualityOfServiceLevel.AtLeastOnce);
        }

        await collector.WaitForCountAsync(10);

        Assert.Equal(
            Enumerable.Range(0, 10).Select(index => index.ToString()),
            collector.Messages.Select(message => message.GetPayloadAsString()));
    }

    [Fact]
    public async Task Receive_AFailingSubscriberDoesNotStopTheOthers()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var logger = new TestLogger();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port), logger);
        var collector = new MqttMessageCollector(client);
        client.MessageReceived += (_, _) => throw new InvalidOperationException("subscriber failure");
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("a/b")).IsSuccess);

        await broker.InjectAsync("a/b", "x");
        await collector.WaitForCountAsync(1);

        Assert.Equal("x", collector.Messages[0].GetPayloadAsString());
        // 收集器先于抛异常的订阅者被调用，错误日志在它之后才写：等它出现，而不是立即断言
        // （全量并行时曾在日志写入之前断言，2026-10-25 test-affected 第 1 遍）。
        await Polling.WaitUntilAsync(() => logger.Entries.Any(entry => entry.Level == LogLevel.Error), 5000);
    }

    [Fact]
    public async Task Receive_NotSubscribed_GetsNothing()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        var collector = new MqttMessageCollector(client);
        await client.ConnectAndAssertAsync();

        await broker.InjectAsync("a/b", "x");
        await collector.ExpectSilenceAsync();
    }
}