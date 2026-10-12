using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 重连在"应用断开后又连上"与"恢复订阅时链路出问题"两种情形下仍然可靠（P61-D9；审阅者验收时补充）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class MqttReconnectRecoveryTests
{
    // DisconnectAsync 只停止当前这一轮重连，不能把重连永久关掉；否则再次连上之后的断线会让状态永远停在 Reconnecting。
    [Fact]
    public async Task Reconnect_StillWorksAfterTheApplicationDisconnectedAndConnectedAgain()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "recovery-1").Reconnecting();
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();
        await client.DisconnectAsync();
        await client.ConnectAndAssertAsync();

        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("recovery-1");

        await MqttReconnectWait.ReconnectedAsync(client, broker, after);
        Assert.Equal(MqttConnectionState.Connected, client.State);
    }

    // 只有 Broker 拒绝的订阅才移出订阅表；恢复时超时或断线是链路问题，订阅要保留，并且这一轮重连算失败、继续重试。
    [Fact]
    public async Task Reconnect_WhenRestoringASubscriptionTimesOut_KeepsItAndRetries()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "recovery-2").Reconnecting();
        options.Timeout = 500;
        var logger = new TestLogger();
        using var client = MqttClient.Create(options, logger);
        var collector = new MqttMessageCollector(client);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("slow/#")).IsSuccess);

        broker.DelayNextSubscription("slow/#", TimeSpan.FromMilliseconds(1500));
        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("recovery-2");
        await MqttReconnectWait.ReconnectedAsync(client, broker, after);

        // 再断一次（Clean Session，Broker 不保留订阅）：订阅仍在表里，这一轮会再次恢复它，因此消息还能收到。
        int second = broker.Connects.Count;
        await broker.DisconnectClientAsync("recovery-2");
        await MqttReconnectWait.ReconnectedAsync(client, broker, second);
        await broker.InjectAsync("slow/line1", "still subscribed");
        await collector.WaitForCountAsync(1);

        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("forgotten", StringComparison.Ordinal));
    }

    // P61-D7：Broker 拒绝的订阅不进订阅表，因此重连之后不会被恢复。拒绝只生效一次，所以只要被记入了表，重连时就会再订阅成功。
    [Fact]
    public async Task Reconnect_DoesNotRestoreASubscriptionTheBrokerRejected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "recovery-4", MqttProtocolVersion.V500).Reconnecting();
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();
        broker.RejectSubscription("bad/#");
        Assert.Equal(CommErrorKind.RemoteError, (await client.SubscribeAsync("bad/#")).ErrorKind);
        Assert.True((await client.SubscribeAsync("good/#")).IsSuccess);
        int rejectedBefore = SubscribeCount(broker, "bad/#");

        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("recovery-4");
        await MqttReconnectWait.ReconnectedAsync(client, broker, after);

        // good/# 排在 bad/# 之后：它恢复了，说明 bad/# 若在表里也早已被重新订阅过。
        await Polling.WaitUntilAsync(() => SubscribeCount(broker, "good/#") >= 2);
        Assert.Equal(rejectedBefore, SubscribeCount(broker, "bad/#"));
    }

    // 应用断开之后，即使 Broker 回来了，也不能再有重连（计划第 9 节）。
    [Fact]
    public async Task Disconnect_DuringReconnecting_NoConnectionArrivesAfterTheBrokerComesBack()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "recovery-3").Reconnecting(40, 200);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();
        await broker.StopAsync();
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Reconnecting);

        await client.DisconnectAsync();

        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        await using TestBroker restarted = await TestBroker.StartOnPortAsync(broker.Port);
        await Task.Delay(400);
        Assert.Empty(restarted.Connects);
    }

    private static int SubscribeCount(TestBroker broker, string topicFilter) =>
        broker.Subscriptions.Count(item => item.Topic == topicFilter);
}
