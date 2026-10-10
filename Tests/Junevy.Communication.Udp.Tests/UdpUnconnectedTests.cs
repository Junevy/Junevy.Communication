using System.Collections.Concurrent;
using System.Net;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Udp.Tests;

/// <summary>非定向模式（只绑定本地端口，用 SendToAsync / RequestToAsync 与任意地址通讯）的端到端测试。</summary>
[Collection(SocketTimingCollection.Name)]
public sealed class UdpUnconnectedTests
{
    [Fact(Timeout = 30000)]
    public async Task Unconnected_RequestTo_TwoPeers()
    {
        using ScriptedUdpPeer first = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(Tagged(1, datagram.Buffer)));
        using ScriptedUdpPeer second = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(Tagged(2, datagram.Buffer)));
        await using var channel = new UdpChannel(UdpTestConfig.Unconnected());
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        CommResult<byte[]> fromFirst = await channel.RequestToAsync(first.EndPoint, new byte[] { 9 });
        CommResult<byte[]> fromSecond = await channel.RequestToAsync(second.EndPoint, new byte[] { 9 });

        Assert.True(fromFirst.IsSuccess, fromFirst.ToString());
        Assert.Equal(new byte[] { 1, 9 }, fromFirst.Data);
        Assert.True(fromSecond.IsSuccess, fromSecond.ToString());
        Assert.Equal(new byte[] { 2, 9 }, fromSecond.Data);
    }

    [Fact(Timeout = 30000)]
    public async Task Unconnected_SendAsync_ReturnsInvalidRequest()
    {
        await using var channel = new UdpChannel(UdpTestConfig.Unconnected());
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        CommResult sent = await channel.SendAsync(new byte[] { 1 });
        CommResult<byte[]> request = await channel.RequestAsync(new byte[] { 1 });

        Assert.Equal(CommErrorKind.InvalidRequest, sent.ErrorKind);
        Assert.Contains("SendToAsync", sent.ErrorMessage);
        Assert.Equal(CommErrorKind.InvalidRequest, request.ErrorKind);
        Assert.Contains("RequestToAsync", request.ErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task Unconnected_ReceiveAsync_AcceptsAnySource()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        await using var channel = new UdpChannel(UdpTestConfig.Unconnected());
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        Task<CommResult<byte[]>> receive = channel.ReceiveAsync(new RequestOptions { Timeout = 5000 });
        await peer.SendToAsync(channel.LocalEndPoint!, new byte[] { 3, 3 });

        CommResult<byte[]> frame = await receive;
        Assert.True(frame.IsSuccess, frame.ToString());
        Assert.Equal(new byte[] { 3, 3 }, frame.Data);
    }

    [Fact(Timeout = 30000)]
    public async Task Unconnected_SendToAsync_DeliversToPeer()
    {
        var received = new ConcurrentQueue<byte[]>();
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) =>
        {
            received.Enqueue(datagram.Buffer);
            return Task.FromResult<byte[]?>(null);
        });
        await using var channel = new UdpChannel(UdpTestConfig.Unconnected());
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        CommResult sent = await channel.SendToAsync(peer.EndPoint, new byte[] { 6 });

        Assert.True(sent.IsSuccess, sent.ToString());
        await Polling.WaitUntilAsync(() => received.Count == 1);
        Assert.True(received.TryPeek(out byte[]? datagram));
        Assert.Equal(new byte[] { 6 }, datagram);
    }

    private static byte[] Tagged(byte tag, byte[] data)
    {
        var result = new byte[data.Length + 1];
        result[0] = tag;
        Buffer.BlockCopy(data, 0, result, 1, data.Length);
        return result;
    }
}
