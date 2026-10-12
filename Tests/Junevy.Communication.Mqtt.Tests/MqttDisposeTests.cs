using Junevy.Communication.Mqtt.Tests.TestSupport;

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

    [Fact]
    public async Task Dispose_NotConnected_Completes()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        client.Dispose();

        Assert.Empty(broker.Connects);
    }
}