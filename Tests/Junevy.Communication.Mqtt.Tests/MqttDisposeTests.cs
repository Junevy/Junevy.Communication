using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttDisposeTests
{
    [Fact]
    public async Task Dispose_CanBeCalledTwice()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        await client.ConnectAndAssertAsync();
        client.Dispose();
        client.Dispose();

        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledTwice()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Connect_AfterDispose_ThrowsObjectDisposed()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Disconnect_AfterDispose_ThrowsObjectDisposed()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DisconnectAsync());
    }

    // P6-C8 与计划第 15 节：释放之后的发布、订阅、退订返回 ConnectionClosed，不抛异常。
    [Fact]
    public async Task MessagingOperations_AfterDispose_ReportConnectionClosed()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();
        await client.DisposeAsync();

        Assert.Equal(CommErrorKind.ConnectionClosed, (await client.PublishAsync(new MqttMessage("a/b", "x"))).ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, (await client.SubscribeAsync("a/b")).ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, (await client.UnsubscribeAsync("a/b")).ErrorKind);
    }

    // 连接进行中释放：那次 ConnectAsync 返回失败结果（不抛异常），释放之后状态停在 Disconnected。
    [Fact]
    public async Task Dispose_WhileConnecting_TheConnectReturnsAFailureAndTheStateStaysDisconnected()
    {
        using SilentTcpServer server = SilentTcpServer.Start();
        MqttClientOptions options = MqttTestOptions.Valid(server.Port);
        options.Timeout = 1500;
        var client = MqttClient.Create(options);
        Task<CommResult> connecting = client.ConnectAsync();
        await Polling.WaitUntilAsync(() => server.AcceptedConnectionCount > 0);

        await client.DisposeAsync();
        CommResult result = await connecting;

        Assert.False(result.IsSuccess);
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Dispose_NotConnected_Completes()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        client.Dispose();

        Assert.Empty(broker.Connects);
    }
}