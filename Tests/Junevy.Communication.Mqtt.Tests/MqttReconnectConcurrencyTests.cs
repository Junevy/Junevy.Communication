using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 重连期间的并发、停止与遗嘱（P61-D9、D11、D14）。与状态迁移、订阅恢复相关的用例放在 <see cref="MqttReconnectTests"/>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class MqttReconnectConcurrencyTests
{
    [Fact]
    public async Task Reconnect_ConcurrentPublishes_OnlyProduceTheThreeExpectedOutcomes()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-12").Reconnecting(40, 200);
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        var stop = new CancellationTokenSource();
        var outcomes = new List<CommErrorKind>();
        Task publisher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                CommResult result = await client.PublishAsync(new MqttMessage("a/b", "x"));
                lock (outcomes)
                {
                    outcomes.Add(result.IsSuccess ? CommErrorKind.None : result.ErrorKind);
                }
            }
        });

        for (int round = 0; round < 3; round++)
        {
            int after = broker.Connects.Count;
            await broker.DisconnectClientAsync("reconnect-12");
            await MqttReconnectWait.ReconnectedAsync(client, broker, after);
        }

        stop.Cancel();
        await publisher;

        Assert.NotEmpty(outcomes);
        Assert.All(outcomes, kind => Assert.True(
            kind == CommErrorKind.None || kind == CommErrorKind.ConnectionClosed || kind == CommErrorKind.NotConnected,
            $"Unexpected outcome {kind}."));

        await AssertNoUnobservedTaskExceptionAsync();
    }

    [Fact]
    public async Task Reconnect_DisposeDuringReconnecting_WaitsForTheLoopToExit()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-13").Reconnecting(40, 200);
        var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        await broker.StopAsync();
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Reconnecting);

        await client.DisposeAsync();

        // 释放返回时循环已经退出：Broker 即使恢复，也不会再有连接进来（Q40）。
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        await using TestBroker restarted = await TestBroker.StartOnPortAsync(broker.Port);
        await Task.Delay(400);
        Assert.Empty(restarted.Connects);
    }

    [Fact]
    public async Task Reconnect_CarriesTheWillOnEveryConnection()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "reconnect-14").Reconnecting(40, 200);
        options.WillTopic = "devices/1/status";
        options.WillPayload = "offline";
        using var client = MqttClient.Create(options);
        await client.ConnectAndAssertAsync();

        using var watcher = MqttClient.Create(MqttTestOptions.Valid(broker.Port, "will-watcher"));
        var collector = new MqttMessageCollector(watcher);
        await watcher.ConnectAndAssertAsync();
        Assert.True((await watcher.SubscribeAsync("devices/1/status")).IsSuccess);

        int after = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-14");
        await MqttReconnectWait.ReconnectedAsync(client, broker, after);
        await collector.WaitForCountAsync(1);
        Assert.Equal("offline", collector.Messages[0].GetPayloadAsString());

        // 第二次异常断开仍然带遗嘱，证明每次连接都携带了它（P61-D14）。
        int second = broker.Connects.Count;
        await broker.DisconnectClientAsync("reconnect-14");
        await MqttReconnectWait.ReconnectedAsync(client, broker, second);
        await collector.WaitForCountAsync(2);
        Assert.Equal("offline", collector.Messages[1].GetPayloadAsString());
    }


    // Q40：强制 GC 之后没有未观察的任务异常。
    private static async Task AssertNoUnobservedTaskExceptionAsync()
    {
        int unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) => Interlocked.Increment(ref unobserved);

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Equal(0, unobserved);
    }
}