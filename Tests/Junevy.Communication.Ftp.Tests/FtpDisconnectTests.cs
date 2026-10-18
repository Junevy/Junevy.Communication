using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 断开（P63-D3）：<c>DisconnectAsync</c> 发出 QUIT，<c>IsConnected</c> 随之变为 false；可以重复调用。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpDisconnectTests
{
    [Fact]
    public async Task Disconnect_SendsQuit_AndClearsIsConnected()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        await client.ConnectAsync();

        await client.DisconnectAsync();

        Assert.False(client.IsConnected);
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "QUIT");
    }

    [Fact]
    public async Task Disconnect_CanBeCalledRepeatedly()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        await client.ConnectAsync();

        await client.DisconnectAsync();
        await client.DisconnectAsync();

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Disconnect_WhenNeverConnected_DoesNothing()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        await client.DisconnectAsync();

        Assert.Equal(0, server.ConnectionCount);
        Assert.False(client.IsConnected);
    }
}
