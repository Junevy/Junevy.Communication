using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Udp.Tests;

/// <summary>
/// 握手（<see cref="IConnectionInitializer"/>）的端到端测试：握手期间到达的数据报进入积压并在握手结束后派发（D8），
/// 握手超时或失败使本次打开失败。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class UdpHandshakeTests
{
    [Fact(Timeout = 30000)]
    public async Task Handshake_DatagramsDuringHandshake_AreDeliveredAfterIt()
    {
        // 对端收到 HELLO 后，先发一个 EXTRA（在握手完成之前到达），再回复 WELCOME。
        ScriptedUdpPeer? peer = null;
        peer = ScriptedUdpPeer.Start(async (index, datagram) =>
        {
            await peer!.SendToAsync((IPEndPoint)datagram.RemoteEndPoint, Encoding.ASCII.GetBytes("EXTRA"));
            return Encoding.ASCII.GetBytes("WELCOME");
        });
        using (peer)
        {
            var frames = new ConcurrentQueue<byte[]>();
            await using var channel = new UdpChannel(UdpTestConfig.Directed(peer), null, new ChannelComponents { Initializer = new HelloWelcomeInitializer() });
            channel.FrameReceived += (sender, args) => frames.Enqueue(args.Data);

            CommResult connected = await channel.ConnectAsync();

            Assert.True(connected.IsSuccess, connected.ToString());
            Assert.Equal(ConnectionState.Connected, channel.State);
            await Polling.WaitUntilAsync(() => frames.Count == 1);
            Assert.True(frames.TryPeek(out byte[]? frame));
            Assert.Equal("EXTRA", Encoding.ASCII.GetString(frame!));
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Handshake_Timeout_FailsOpen()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        var config = UdpTestConfig.Directed(peer);
        config.HandshakeTimeout = 300;
        await using var channel = new UdpChannel(config, null, new ChannelComponents { Initializer = new NeverFinishingInitializer() });

        CommResult connected = await channel.ConnectAsync();

        Assert.Equal(CommErrorKind.Timeout, connected.ErrorKind);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Null(channel.LocalEndPoint);
    }

    [Fact(Timeout = 30000)]
    public async Task Handshake_InitializerFailure_FailsOpen()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        await using var channel = new UdpChannel(UdpTestConfig.Directed(peer), null, new ChannelComponents { Initializer = new RejectingInitializer() });

        CommResult connected = await channel.ConnectAsync();

        Assert.Equal(CommErrorKind.AuthenticationFailed, connected.ErrorKind);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    // 发送 HELLO，然后只接收 WELCOME（其他数据报留给积压）。
    private sealed class HelloWelcomeInitializer : IConnectionInitializer
    {
        public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
        {
            CommResult sent = await channel.SendAsync(Encoding.ASCII.GetBytes("HELLO"), cancellationToken).ConfigureAwait(false);
            if (!sent.IsSuccess)
                return sent;

            CommResult<byte[]> welcome = await channel.ReceiveAsync(
                new RequestOptions { Timeout = 3000, Matcher = new ExactMatcher("WELCOME") }, cancellationToken).ConfigureAwait(false);
            return welcome.IsSuccess ? CommResult.Success() : welcome.ToResult();
        }
    }

    private sealed class NeverFinishingInitializer : IConnectionInitializer
    {
        public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return CommResult.Success();
        }
    }

    private sealed class RejectingInitializer : IConnectionInitializer
    {
        public Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
            => Task.FromResult(CommResult.Fail("The peer rejected the login.", CommErrorKind.AuthenticationFailed));
    }

    // 只匹配完全相等的数据报。
    private sealed class ExactMatcher : IResponseMatcher
    {
        private readonly byte[] expected;

        public ExactMatcher(string text)
        {
            expected = Encoding.ASCII.GetBytes(text);
        }

        public bool IsMatch(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame) => frame.SequenceEqual(expected);
    }
}
