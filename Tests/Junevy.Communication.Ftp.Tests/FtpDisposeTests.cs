using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 释放的规则（P6-C8）：<c>ConnectAsync</c> 与 <c>DisconnectAsync</c> 在释放之后抛 <see cref="ObjectDisposedException"/>；
/// <c>Dispose</c> 与 <c>DisposeAsync</c> 可以重复调用。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpDisposeTests
{
    [Fact]
    public async Task Dispose_CanBeCalledRepeatedly_InAnyOrder()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        var client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        client.Dispose();
        client.Dispose();
        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAfterDispose_ThrowsObjectDisposedException()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        var client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task DisconnectAfterDispose_ThrowsObjectDisposedException()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        var client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DisconnectAsync());
    }

    [Fact]
    public async Task DisposeWhileConnected_SendsQuit_BestEffort()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        var client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        await client.ConnectAsync();

        await client.DisposeAsync();

        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "QUIT");
        Assert.False(client.IsConnected);
    }
}
