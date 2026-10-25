using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// UDP 页在回环地址上的端到端测试（计划 Task 5 第 2 条）：本地端口由系统选择（0），对端是测试里的 <see cref="UdpClient"/>。
/// </summary>
public sealed class UdpPageTests
{
    [Fact]
    public async Task Directed_Request_GetsTheReplyFromTheRemote()
    {
        var dispatcher = new ManualUiDispatcher();
        using var peer = ChannelTestKit.UdpPeer();
        Task echo = EchoOnceAsync(peer);
        await using var page = ChannelTestKit.UdpPage(dispatcher);
        page.Directed = true;
        page.RemoteHost = "127.0.0.1";
        page.RemotePort = ChannelTestKit.PortOf(peer);
        await page.ConnectCommand.ExecuteAsync(null);

        page.PayloadText = "hex:01 02";
        await page.RequestCommand.ExecuteAsync(null);

        await echo.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x01, 0x02 }, Assert.Single(page.ReceivedFrames).Data);
    }

    [Fact]
    public async Task Undirected_Send_DeliversTheDatagramToTheTarget()
    {
        var dispatcher = new ManualUiDispatcher();
        using var peer = ChannelTestKit.UdpPeer();
        Task<UdpReceiveResult> incoming = peer.ReceiveAsync();
        await using var page = ChannelTestKit.UdpPage(dispatcher);
        page.Directed = false;
        page.TargetHost = "127.0.0.1";
        page.TargetPort = ChannelTestKit.PortOf(peer);
        await page.ConnectCommand.ExecuteAsync(null);

        page.PayloadText = "hex:AB CD";
        await page.SendCommand.ExecuteAsync(null);

        UdpReceiveResult result = await incoming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, result.Buffer);
        Assert.Equal(new[] { "hex:AB CD" }, page.SendHistory);
    }

    [Fact]
    public async Task Undirected_Request_GetsTheReplyFromTheTarget()
    {
        var dispatcher = new ManualUiDispatcher();
        using var peer = ChannelTestKit.UdpPeer();
        Task echo = EchoOnceAsync(peer);
        await using var page = ChannelTestKit.UdpPage(dispatcher);
        page.Directed = false;
        page.TargetHost = "127.0.0.1";
        page.TargetPort = ChannelTestKit.PortOf(peer);
        await page.ConnectCommand.ExecuteAsync(null);

        page.PayloadText = "hex:0A";
        await page.RequestCommand.ExecuteAsync(null);

        await echo.WaitAsync(TimeSpan.FromSeconds(5));
        ReceivedFrame frame = Assert.Single(page.ReceivedFrames);
        Assert.Equal(new byte[] { 0x0A }, frame.Data);
        Assert.Equal(page.Target, frame.Source);
    }

    [Fact]
    public async Task Undirected_WithTheBuiltInHeartbeat_IsRefusedAtConnect_WithAReason()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.UdpPage(dispatcher);
        page.Directed = false;
        page.Settings.Heartbeat.Enabled = true;
        page.Settings.Heartbeat.Payload = "hex:00";
        page.Settings.Heartbeat.ExpectedReply = "hex:01";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal("连接", record.Operation);
        Assert.False(record.Succeeded);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
        Assert.False(string.IsNullOrWhiteSpace(record.Message), "库说明了不能使用内置心跳的原因。");
    }

    [BroadcastFact]
    public async Task Broadcast_IsDeliveredToTheLocalListener_WhenEnabled()
    {
        var dispatcher = new ManualUiDispatcher();
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        Task<UdpReceiveResult> incoming = listener.ReceiveAsync();
        await using var page = ChannelTestKit.UdpPage(dispatcher);
        page.Directed = false;
        page.EnableBroadcast = true;
        page.TargetHost = "255.255.255.255";
        page.TargetPort = ChannelTestKit.PortOf(listener);
        await page.ConnectCommand.ExecuteAsync(null);

        page.PayloadText = "hex:42";
        await page.SendCommand.ExecuteAsync(null);

        UdpReceiveResult result = await incoming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x42 }, result.Buffer);
    }

    /// <summary>接收一个数据报并原样发回给发送方。</summary>
    private static async Task EchoOnceAsync(UdpClient peer)
    {
        UdpReceiveResult received = await peer.ReceiveAsync();
        await peer.SendAsync(received.Buffer, received.RemoteEndPoint);
    }
}
