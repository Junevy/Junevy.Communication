using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 重连的状态迁移与订阅恢复（P61-D9）。并发、停止与遗嘱相关的用例在 <see cref="MqttReconnectConcurrencyTests"/>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class MqttReconnectTests
{
    // Broker 在同一个端口还活着时，重连非常快，State 可能一闪而过。
    [Fact]
    public async Task Reconnect_AfterTheBrokerDropsTheClient_GoesReconnectingThenConnected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-1").Reconnecting();
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        var states = new List<MqttConnectionState>();
        client.StateChanged += (_, args) =>
        {
            lock (states)
            {
                states.Add(args.Current);
            }
        };

        int connectsBefore = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-1");

        await MqttReconnectWait.ReconnectedAsync(client, broker, connectsBefore);
        lock (states)
        {
            Assert.Equal(
                new[] { MqttConnectionState.Reconnecting, MqttConnectionState.Connected },
                states);
        }
    }

    [Fact]
    public async Task Reconnect_RestoresSubscriptionsSoMessagesArriveAgain()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-2").Reconnecting();
        using var client = MqttClient.Create(options);
        var collector = new MqttMessageCollector(client);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("plant/#")).IsSuccess);
        await broker.InjectAsync("plant/line1/temp", "before");
        await collector.WaitForCountAsync(1);

        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-2");
        await MqttReconnectWait.ReconnectedAsync(client, broker, after);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count >= 2);

        await broker.InjectAsync("plant/line1/temp", "after");
        await collector.WaitForCountAsync(2);
        Assert.Equal("after", collector.Messages[1].GetPayloadAsString());
    }

    [Fact]
    public async Task Reconnect_ReportsConnectedOnlyAfterEverySubscriptionWasRestored()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-3").Reconnecting();
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("a/b")).IsSuccess);
        Assert.True((await client.SubscribeAsync("c/d")).IsSuccess);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count >= 2);

        var subscriptionsWhenConnected = -1;
        client.StateChanged += (_, args) =>
        {
            if (args.Current == MqttConnectionState.Connected)
            {
                subscriptionsWhenConnected = broker.Subscriptions.Count;
            }
        };

        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-3");
        await MqttReconnectWait.ReconnectedAsync(client, broker, after);

        // P61-D5：宿主看到 Connected 时订阅已经恢复完毕。
        Assert.Equal(4, subscriptionsWhenConnected);
    }

    [Fact]
    public async Task Reconnect_WithCleanSession_ResubscribesEveryFilterInOrder()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-4").Reconnecting();
        options.CleanSession = true;
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("first/#")).IsSuccess);
        Assert.True((await client.SubscribeAsync("second/#")).IsSuccess);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count >= 2);

        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-4");
        await MqttReconnectWait.ReconnectedAsync(client, broker, after);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count >= 4);

        Assert.Equal(new[] { "first/#", "second/#" }, broker.Subscriptions.Skip(2).Select(item => item.Topic));
    }

    [Fact]
    public async Task Reconnect_WithPersistentSession_DoesNotResubscribe()
    {
        await using TestBroker broker = await TestBroker.StartAsync(persistentSessions: true);
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-5").Reconnecting();
        options.CleanSession = false;
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("kept/#")).IsSuccess);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count >= 1);

        await broker.DisconnectClientAsync("reconnect-5");
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Connected);
        await Task.Delay(300);

        // Session Present 为 true 时不重新订阅（P61-D9）。
        Assert.Single(broker.Subscriptions);
    }

    [Fact]
    public async Task Reconnect_WhenOneFilterIsRejected_ForgetsItAndKeepsTheOthers()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-6", MqttProtocolVersion.V500).Reconnecting();
        var logger = new TestLogger();
        using var client = MqttClient.Create(options, logger);
        await client.ConnectAndAssertAsync();
        Assert.True((await client.SubscribeAsync("good/#")).IsSuccess);
        Assert.True((await client.SubscribeAsync("bad/#")).IsSuccess);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count >= 2);

        broker.RejectSubscription("bad/#");

        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-6");
        await MqttReconnectWait.ReconnectedAsync(client, broker, after);
        await Polling.WaitUntilAsync(() => broker.Subscriptions.Count >= 3);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("forgotten", StringComparison.Ordinal));

        // 再断一次：坏订阅已被移出订阅表，因此不会再试一次（记号：bad/# 全程只出现一次）。
        int third = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-6");
        await MqttReconnectWait.ReconnectedAsync(client, broker, third);
        await Task.Delay(200);
        Assert.Single(broker.Subscriptions, item => item.Topic == "bad/#");
        Assert.Equal("good/#", broker.Subscriptions.Last().Topic);
    }

    [Fact]
    public async Task Reconnect_AfterTheBrokerComesBack_EntersConnected()
    {
        int port;
        await using (TestBroker broker = await TestBroker.StartAsync())
        {
            port = broker.Port;
            MqttClientOptions options = MqttTestOptions.Valid(port, "reconnect-7").Reconnecting(40, 200);
            using var client = MqttClient.Create(options);
            await client.ConnectAndAssertAsync();
            await broker.StopAsync();

            await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Reconnecting);
            Assert.True(broker.Connects.Count >= 1);

            await using TestBroker restarted = await TestBroker.StartOnPortAsync(port);
            await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Connected, 15000);
            Assert.Equal(MqttConnectionState.Connected, client.State);
        }
    }

    [Fact]
    public async Task Reconnect_WhenDisabled_GoesStraightToDisconnected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-8");
        options.AutoReconnect = false;
        options.ReconnectInterval = 40;
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        string? reason = null;
        client.StateChanged += (_, args) =>
        {
            if (args.Current == MqttConnectionState.Disconnected)
            {
                reason = args.Reason;
            }
        };

        await broker.DisconnectClientAsync("reconnect-8");
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Disconnected);
        Assert.False(string.IsNullOrEmpty(reason));

        int connectsAfterDrop = broker.Connects.Count;
        await Task.Delay(300);
        Assert.Equal(connectsAfterDrop, broker.Connects.Count);
    }

    [Fact]
    public async Task Reconnect_DisconnectDuringReconnecting_StopsTheLoop()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-9").Reconnecting(40, 200);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        await broker.StopAsync();
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Reconnecting);

        await client.DisconnectAsync();

        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        await Task.Delay(300);
    }

    [Fact]
    public async Task Reconnect_ConnectWhileReconnecting_ReportsInvalidRequest()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-10").Reconnecting(40, 200);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        await broker.StopAsync();
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Reconnecting);

        CommResult result = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    [Fact]
    public async Task Reconnect_OperationsDuringReconnecting_ReportNotConnected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-11").Reconnecting(40, 200);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        await broker.StopAsync();
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Reconnecting);

        Assert.Equal(CommErrorKind.NotConnected, (await client.PublishAsync(new MqttMessage("a/b", "x"))).ErrorKind);
        Assert.Equal(CommErrorKind.NotConnected, (await client.SubscribeAsync("a/b")).ErrorKind);
        Assert.Equal(CommErrorKind.NotConnected, (await client.UnsubscribeAsync("a/b")).ErrorKind);
    }
}
