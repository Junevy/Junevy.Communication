using System.Net;
using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Logging;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Logging;

/// <summary>
/// 锁定日志模板（计划 Task 2 第 4 条）：用库的真实通道收发报文，页签必须得到方向、长度与十六进制都正确的报文。
/// 模板一变，这里失败，而不是报文悄悄不再显示。
/// </summary>
public sealed class LibraryFrameLogTests
{
    [Fact]
    public async Task TcpClient_LogsStreamFrames_WithTheLibraryTemplates()
    {
        int port = FreePort.Get();
        await using var server = new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port });
        server.FrameReceived += async (_, e) => await e.Session.SendAsync(Encoding.ASCII.GetBytes("PONG"));
        Assert.True((await server.StartAsync()).IsSuccess);
        await using SessionViewModel session = TestShell.CreateSession();
        await using var client = new TcpClientChannel(
            new TcpClientChannelConfig { Host = "127.0.0.1", Port = port, ConnectTimeout = 10000, RequestTimeout = 5000 },
            new Logger<TcpClientChannel>(session.SessionLoggerFactory));
        Assert.True((await client.ConnectAsync()).IsSuccess);

        CommResult<byte[]> reply = await client.RequestAsync(Encoding.ASCII.GetBytes("PING"));

        Assert.True(reply.IsSuccess, reply.ErrorMessage);
        session.Log.Flush();
        FrameEntry tx = Assert.Single(session.Log.Visible.OfType<FrameEntry>(), f => f.Direction == FrameDirection.Tx);
        Assert.Equal(4, tx.Length);
        Assert.Equal("50-49-4E-47", tx.Hex);
        Assert.Null(tx.Endpoint);
        FrameEntry rx = Assert.Single(session.Log.Visible.OfType<FrameEntry>(), f => f.Direction == FrameDirection.Rx);
        Assert.Equal(4, rx.Length);
        Assert.Equal("50-4F-4E-47", rx.Hex);
    }

    [Fact]
    public async Task TcpClient_LongFrame_IsTruncatedInTheLogAndKeepsItsLength()
    {
        int port = FreePort.Get();
        await using var server = new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port });
        Assert.True((await server.StartAsync()).IsSuccess);
        await using SessionViewModel session = TestShell.CreateSession();
        await using var client = new TcpClientChannel(
            new TcpClientChannelConfig { Host = "127.0.0.1", Port = port, ConnectTimeout = 10000 },
            new Logger<TcpClientChannel>(session.SessionLoggerFactory));
        Assert.True((await client.ConnectAsync()).IsSuccess);

        CommResult sent = await client.SendAsync(new byte[300]);

        Assert.True(sent.IsSuccess, sent.ErrorMessage);
        session.Log.Flush();
        FrameEntry frame = Assert.Single(session.Log.Visible.OfType<FrameEntry>());
        Assert.True(frame.Truncated);
        Assert.Equal(300, frame.Length);
        Assert.EndsWith("...(+44 bytes)", frame.Hex, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UdpChannel_LogsDatagramFrames_WithTheDestinationAndRemote()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        await using var local = new UdpChannel(
            new UdpChannelConfig { LocalPort = 0, RequestTimeout = 3000 },
            new Logger<UdpChannel>(session.SessionLoggerFactory));
        await using var peer = new UdpChannel(new UdpChannelConfig { LocalPort = 0, RequestTimeout = 3000 });
        Assert.True((await local.ConnectAsync()).IsSuccess);
        Assert.True((await peer.ConnectAsync()).IsSuccess);
        peer.FrameReceived += async (_, e) => await peer.SendToAsync((IPEndPoint)e.RemoteEndPoint!, Encoding.ASCII.GetBytes("PONG"));
        var destination = new IPEndPoint(IPAddress.Loopback, peer.LocalEndPoint!.Port);

        CommResult<byte[]> reply = await local.RequestToAsync(destination, Encoding.ASCII.GetBytes("PING"));

        Assert.True(reply.IsSuccess, reply.ErrorMessage);
        session.Log.Flush();
        FrameEntry tx = Assert.Single(session.Log.Visible.OfType<FrameEntry>(), f => f.Direction == FrameDirection.Tx);
        Assert.Equal("50-49-4E-47", tx.Hex);
        Assert.Equal(destination.ToString(), tx.Endpoint);
        FrameEntry rx = Assert.Single(session.Log.Visible.OfType<FrameEntry>(), f => f.Direction == FrameDirection.Rx);
        Assert.Equal("50-4F-4E-47", rx.Hex);
        Assert.Equal(destination.ToString(), rx.Endpoint);
    }
}
