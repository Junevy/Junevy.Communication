using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// 握手钩子在真实的回环连接上运行（计划 DA-D12）：库在每次 <c>ConnectAsync</c> 时执行它，成功或失败都由连接结果体现。
/// </summary>
public sealed class HandshakeLoopbackTests
{
    [Fact]
    public async Task Handshake_RunsDuringConnect_AndTheServerReplyMatches()
    {
        int port = FreePort.Get();
        await using var server = new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port });
        server.FrameReceived += async (_, e) => await e.Session.SendAsync("ACK"u8.ToArray());
        Assert.True((await server.StartAsync()).IsSuccess);
        await using var client = Client(port, new ScriptedInitializer([0x02], "ACK"u8.ToArray(), replyTimeout: 3000));

        CommResult connected = await client.ConnectAsync();

        Assert.True(connected.IsSuccess, connected.ErrorMessage);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Handshake_WithAWrongReply_FailsTheConnect_WithAProtocolViolation()
    {
        int port = FreePort.Get();
        await using var server = new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port });
        server.FrameReceived += async (_, e) => await e.Session.SendAsync("NO"u8.ToArray());
        Assert.True((await server.StartAsync()).IsSuccess);
        await using var client = Client(port, new ScriptedInitializer([0x02], "ACK"u8.ToArray(), replyTimeout: 3000));

        CommResult connected = await client.ConnectAsync();

        Assert.False(connected.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, connected.ErrorKind);
    }

    [Fact]
    public async Task Handshake_WithoutAReplyCheck_OnlySends()
    {
        int port = FreePort.Get();
        await using var server = new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port });
        var received = new List<byte[]>();
        server.FrameReceived += (_, e) =>
        {
            lock (received)
            {
                received.Add(e.Data);
            }
        };
        Assert.True((await server.StartAsync()).IsSuccess);
        await using var client = Client(port, new ScriptedInitializer([0x02, 0x03], expectedReply: null, replyTimeout: 3000));

        CommResult connected = await client.ConnectAsync();

        Assert.True(connected.IsSuccess, connected.ErrorMessage);
        await Waiting.UntilAsync(() =>
        {
            lock (received)
            {
                return received.Count > 0;
            }
        }, TimeSpan.FromSeconds(5));
        lock (received)
        {
            Assert.Equal(new byte[] { 0x02, 0x03 }, received[0]);
        }
    }

    private static TcpClientChannel Client(int port, ScriptedInitializer initializer) => new(
        new TcpClientChannelConfig { Host = "127.0.0.1", Port = port, ConnectTimeout = 10000, RequestTimeout = 5000 },
        logger: null,
        components: new TcpChannelComponents { Initializer = initializer });
}
