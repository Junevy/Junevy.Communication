using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Udp.Tests;

/// <summary>接收循环的存活性、超长丢弃、心跳重连、组播与释放语义的端到端测试。</summary>
[Collection(SocketTimingCollection.Name)]
public sealed class UdpLifecycleTests
{
    [Fact(Timeout = 30000)]
    public async Task SendToClosedPort_ReceiveLoopSurvives()
    {
        int closedPort = ClosedPort.Reserve();
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(datagram.Buffer));
        await using var channel = new UdpChannel(UdpTestConfig.Unconnected());
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        CommResult sent = await channel.SendToAsync(new IPEndPoint(IPAddress.Loopback, closedPort), new byte[] { 1 });
        Assert.True(sent.IsSuccess, sent.ToString());

        // 无人监听的端口会返回 ICMP 端口不可达；关闭 SIO_UDP_CONNRESET 时接收循环会因此抛出 10054 并断开。留出时间让报告到达。
        await Task.Delay(300);

        CommResult<byte[]> reply = await channel.RequestToAsync(peer.EndPoint, new byte[] { 2 });
        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(new byte[] { 2 }, reply.Data);
        Assert.Equal(ConnectionState.Connected, channel.State);
    }

    [Fact(Timeout = 30000)]
    public async Task OversizeDatagram_Dropped()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        var config = UdpTestConfig.Directed(peer);
        config.MaxDatagramSize = 100;
        await using var channel = new UdpChannel(config);
        var frames = new ConcurrentQueue<FrameReceivedEventArgs>();
        channel.FrameReceived += (sender, args) => frames.Enqueue(args);
        Assert.True((await channel.ConnectAsync()).IsSuccess);
        IPEndPoint local = channel.LocalEndPoint!;

        await peer.SendToAsync(local, new byte[200]);
        await Polling.WaitUntilAsync(() => channel.Statistics.ProtocolErrors == 1);

        await peer.SendToAsync(local, new byte[10]);
        await Polling.WaitUntilAsync(() => frames.Count == 1);
        Assert.True(frames.TryPeek(out FrameReceivedEventArgs? frame));
        Assert.Equal(10, frame!.Data.Length);
        Assert.Equal(0, channel.Statistics.FramesDropped);
    }

    [MulticastFact(Timeout = 30000)]
    public async Task Multicast_Loopback()
    {
        var config = new UdpChannelConfig
        {
            LocalAddress = "0.0.0.0",
            LocalPort = 0,
            MulticastGroups = new[] { "239.255.0.1" },
            MulticastLoopback = true,
        };
        await using var channel = new UdpChannel(config);
        var frames = new ConcurrentQueue<FrameReceivedEventArgs>();
        channel.FrameReceived += (sender, args) => frames.Enqueue(args);
        CommResult connected = await channel.ConnectAsync();
        Assert.True(connected.IsSuccess, connected.ToString());
        int port = channel.LocalEndPoint!.Port;

        using var sender = new UdpClient(AddressFamily.InterNetwork)
        {
            MulticastLoopback = true,
            Ttl = 1,
        };
        sender.Send(new byte[] { 0x42 }, 1, new IPEndPoint(IPAddress.Parse("239.255.0.1"), port));

        await Polling.WaitUntilAsync(() => frames.Count == 1);
        Assert.True(frames.TryPeek(out FrameReceivedEventArgs? frame));
        Assert.Equal(new byte[] { 0x42 }, frame!.Data);
    }

    [Fact(Timeout = 30000)]
    public async Task Heartbeat_SilentPeer_Reconnecting()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        var config = UdpTestConfig.Directed(peer);
        // 未设置 ExpectedReply 时探测只要求发送成功（UDP 发送几乎总是成功），因此这里要求应答 PONG，对端沉默即为失败。
        config.Heartbeat = new HeartbeatOptions
        {
            Enabled = true,
            Payload = "PING",
            ExpectedReply = "PONG",
            Interval = 100,
            Timeout = 100,
            MaxFailures = 2,
            OnlyWhenIdle = false,
        };
        config.Reconnect = new ReconnectOptions { Enabled = true, Interval = 100, MaxInterval = 200 };
        await using var channel = new UdpChannel(config);
        var changes = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        channel.StateChanged += (sender, args) => changes.Enqueue(args);
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        // 对端不回应心跳：连续失败达到 MaxFailures 后以 HeartbeatFailed 进入后台重连。
        await Polling.WaitUntilAsync(() => changes.Any(args =>
            args.CurrentState == ConnectionState.Reconnecting && args.Reason == DisconnectReason.HeartbeatFailed));

        // 这里只断言首个探测已到达对端。后续探测可能排在请求锁上（迟到窗口与探测超时同长），
        // 被监视器超时后仍计为失败而未发出，因此不能断言到达的探测数量。
        Assert.True(peer.ReceivedCount >= 1);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeFromFrameReceivedHandler_NoDeadlock()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        var channel = new UdpChannel(UdpTestConfig.Directed(peer));
        channel.FrameReceived += (sender, args) => channel.Dispose();
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        // 同步 Dispose 发生在派发上下文内：必须在超时之前完成，不得死锁。
        await peer.SendToAsync(channel.LocalEndPoint!, new byte[] { 1 });
        await Polling.WaitUntilAsync(() => channel.State == ConnectionState.Disposed);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeFromStateChangedHandler_NoDeadlock()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        var channel = new UdpChannel(UdpTestConfig.Directed(peer));
        channel.StateChanged += (sender, args) =>
        {
            if (args.CurrentState == ConnectionState.Connected)
                channel.Dispose();
        };

        Assert.True((await channel.ConnectAsync()).IsSuccess);

        await Polling.WaitUntilAsync(() => channel.State == ConnectionState.Disposed);
    }
}
