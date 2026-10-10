using System.Collections.Concurrent;
using System.Net;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Udp.Tests;

/// <summary>定向模式（配置了 RemoteHost）的端到端测试：真实回环套接字，对端为 <see cref="ScriptedUdpPeer"/>。</summary>
[Collection(SocketTimingCollection.Name)]
public sealed class UdpDirectedTests
{
    [Fact(Timeout = 30000)]
    public async Task Directed_Echo()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(datagram.Buffer));
        await using var channel = new UdpChannel(UdpTestConfig.Directed(peer));
        CommResult connected = await channel.ConnectAsync();
        Assert.True(connected.IsSuccess, connected.ToString());

        CommResult<byte[]> reply = await channel.RequestAsync(new byte[] { 1, 2, 3 });

        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(new byte[] { 1, 2, 3 }, reply.Data);
        Assert.Equal(ConnectionState.Connected, channel.State);
        Assert.NotNull(channel.LocalEndPoint);
        Assert.True(channel.LocalEndPoint!.Port > 0);
    }

    [Fact(Timeout = 30000)]
    public async Task Directed_FiltersOtherSources()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        using ScriptedUdpPeer other = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        await using var channel = new UdpChannel(UdpTestConfig.Directed(peer));
        var frames = new ConcurrentQueue<FrameReceivedEventArgs>();
        channel.FrameReceived += (sender, args) => frames.Enqueue(args);
        Assert.True((await channel.ConnectAsync()).IsSuccess);
        IPEndPoint local = channel.LocalEndPoint!;

        // 另一个对端发来的数据报不触发事件，并计为 FramesDropped。
        await other.SendToAsync(local, new byte[] { 9 });
        await Polling.WaitUntilAsync(() => channel.Statistics.FramesDropped == 1);
        Assert.Empty(frames);

        // 远端发来的数据报正常派发，来源地址为远端。
        await peer.SendToAsync(local, new byte[] { 7 });
        await Polling.WaitUntilAsync(() => frames.Count == 1);
        Assert.True(frames.TryPeek(out FrameReceivedEventArgs? frame));
        Assert.Equal(new byte[] { 7 }, frame!.Data);
        Assert.Equal(peer.EndPoint, frame.RemoteEndPoint);
        Assert.Equal(1, channel.Statistics.FramesDropped);
    }

    [Fact(Timeout = 30000)]
    public async Task Directed_SendAsync_DeliversToPeer()
    {
        var received = new ConcurrentQueue<byte[]>();
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) =>
        {
            received.Enqueue(datagram.Buffer);
            return Task.FromResult<byte[]?>(null);
        });
        await using var channel = new UdpChannel(UdpTestConfig.Directed(peer));
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        CommResult sent = await channel.SendAsync(new byte[] { 4, 5 });

        Assert.True(sent.IsSuccess, sent.ToString());
        await Polling.WaitUntilAsync(() => received.Count == 1);
        Assert.True(received.TryPeek(out byte[]? datagram));
        Assert.Equal(new byte[] { 4, 5 }, datagram);
    }

    [Fact(Timeout = 30000)]
    public async Task Directed_BeforeConnect_ReturnsNotConnected()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(datagram.Buffer));
        await using var channel = new UdpChannel(UdpTestConfig.Directed(peer));

        CommResult sent = await channel.SendAsync(new byte[] { 1 });
        CommResult<byte[]> reply = await channel.RequestAsync(new byte[] { 1 });

        Assert.Equal(CommErrorKind.NotConnected, sent.ErrorKind);
        Assert.Equal(CommErrorKind.NotConnected, reply.ErrorKind);
        Assert.Equal(0, peer.ReceivedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Directed_RequestToOtherAddress_ReturnsInvalidRequest()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(datagram.Buffer));
        using ScriptedUdpPeer other = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(datagram.Buffer));
        await using var channel = new UdpChannel(UdpTestConfig.Directed(peer));
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        // 定向模式只接收远端的应答，因此对其他地址的请求在发送之前即被拒绝。
        CommResult<byte[]> reply = await channel.RequestToAsync(other.EndPoint, new byte[] { 1 });

        Assert.Equal(CommErrorKind.InvalidRequest, reply.ErrorKind);
        Assert.Equal(0, other.ReceivedCount);
    }
}
