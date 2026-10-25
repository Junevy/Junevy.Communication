using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttDisconnectTests
{
    [Fact]
    public async Task Disconnect_WhileConnecting_WaitsForTheAttemptAndLeavesNoConnection()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).DelayConnectAck(TimeSpan.FromMilliseconds(500));
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        Task<CommResult> connecting = client.ConnectAsync();
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Connecting);

        Stopwatch timer = Stopwatch.StartNew();
        await client.DisconnectAsync();
        await connecting;
        timer.Stop();

        // 生命周期锁：断开必须等这次连接尝试结束，因此不会留下半开的连接。
        Assert.True(timer.ElapsedMilliseconds >= 400, $"Disconnect returned after {timer.ElapsedMilliseconds} ms.");
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        // Broker 收到断开后异步清理会话：等它清空，而不是立即断言（全量并行时曾看到尚未清理的 client-1，
        // 2026-10-20 test-affected 第 3 遍）。真留下半开连接时，Broker 在这段时间内不会清理，用例仍然失败。
        await WaitUntilNoClientsAsync(broker);
    }

    private static async Task WaitUntilNoClientsAsync(TestBroker broker)
    {
        Stopwatch waited = Stopwatch.StartNew();
        IReadOnlyList<string> remaining = await broker.ActiveClientIdsAsync();
        while (remaining.Count > 0 && waited.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(50);
            remaining = await broker.ActiveClientIdsAsync();
        }

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Disconnect_AfterConnecting_SendsDisconnectAndCanBeRepeated()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        await client.DisconnectAsync();

        Assert.Equal(MqttConnectionState.Disconnected, client.State);

        // 可以重复调用，不应抛异常，也不应再改变状态。
        await client.DisconnectAsync();
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Disconnect_BeforeConnecting_IsHarmless()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        await client.DisconnectAsync();

        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        Assert.Empty(broker.Connects);
    }

    [Fact]
    public async Task Disconnect_ReportsTheBrokerSawTheClient()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port, "disconnect-1"));
        await client.ConnectAndAssertAsync();

        await client.DisconnectAsync();

        // Broker 处理过 CONNECT，随后连接被关闭；DISCONNECT 本身不经过凭据校验，只能间接验证。
        Assert.Single(broker.Connects);
        await Polling.WaitUntilAsync(() => broker.ConnectedClientIds.Count == 1);
        Assert.Single(broker.ConnectedClientIds);
    }
}