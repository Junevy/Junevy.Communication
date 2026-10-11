using Junevy.Communication.Channels;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 协议客户端的 <c>StateChanged</c> 直接转发通道的事件：处理器收到的是通道引发的同一个参数对象。
/// </summary>
public sealed class PlcClientStateChangedTests
{
    [Fact]
    public void StateChanged_HandlersReceiveTheChannelsOwnEventArgs()
    {
        var channel = new ScriptedClientChannel();
        using var client = new FakePlcClient(channel);
        ConnectionStateChangedEventArgs? received = null;
        client.StateChanged += (_, args) => received = args;
        var raised = new ConnectionStateChangedEventArgs(ConnectionState.Connected, ConnectionState.Disconnected,
            DisconnectReason.RemoteClosed, null, 0);

        channel.RaiseStateChanged(raised);

        Assert.Same(raised, received);
    }
}
