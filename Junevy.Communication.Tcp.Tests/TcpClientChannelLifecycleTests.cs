using System.Diagnostics;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Xunit.Abstractions;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 客户端通道的生命周期与超时的真实套接字测试：重连、心跳、空闲超时、请求超时重建、并发关联、发送超时、断开与释放（计划 10.3）。
/// 时间相关断言使用区间：下限取期望值的 80%，上限取期望值加 2000 毫秒（计划第 1 节第 5 条）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TcpClientChannelLifecycleTests
{
    private readonly ITestOutputHelper output;

    /// <summary>创建测试类（xUnit 注入输出辅助，用于记录实际观察到的数值）。</summary>
    public TcpClientChannelLifecycleTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact(Timeout = 30000)]
    public async Task ServerClosesConnection_Reconnects()
    {
        using var server = ScriptedTcpServer.Start((index, stream, token) => DrainAsync(stream, token));
        var config = CreateConfig(server.Port);
        config.Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 300 };
        await using var channel = new TcpClientChannel(config);
        var log = new StateLog(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

        server.CloseConnection(0);

        StateEntry lost = await log.WaitForAsync(e => e.Current == ConnectionState.Reconnecting, 5000);
        StateEntry restored = await log.WaitForAsync(e => e.Current == ConnectionState.Connected, 5000, lost);
        Assert.Equal(DisconnectReason.RemoteClosed, lost.Reason);
        Assert.InRange(restored.MillisecondsSince(lost), 240.0, 2300.0);

        await WaitUntilAsync(() => server.AcceptedConnectionCount == 2, 5000);
        Assert.True(channel.IsConnected);
    }

    [Fact(Timeout = 30000)]
    public async Task SilentServer_HeartbeatFails_Reconnecting()
    {
        using var server = SilentTcpServer.Start();
        var config = CreateConfig(server.Port);
        // 默认的 ResetOnRequestTimeout 会在第一次探测超时时以 RequestTimeout 断开（心跳探测经由 RequestAsync）。
        // 本测试关闭它，使断开由心跳自身的连续失败计数触发（HeartbeatFailed）。
        config.ResetOnRequestTimeout = false;
        config.Heartbeat = new HeartbeatOptions
        {
            Enabled = true,
            Interval = 200,
            Timeout = 200,
            MaxFailures = 2,
            Payload = "PING",
            ExpectedReply = "PONG",
        };
        config.Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 100 };
        await using var channel = new TcpClientChannel(config);
        var log = new StateLog(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

        StateEntry lost = await log.WaitForAsync(e => e.Current == ConnectionState.Reconnecting, 10000);

        Assert.Equal(DisconnectReason.HeartbeatFailed, lost.Reason);
        Assert.True(server.ReceivedBytes >= "PING".Length, "The heartbeat probes were not received by the silent server.");
        await WaitUntilAsync(() => server.AcceptedConnectionCount >= 2, 10000);
    }

    [Fact(Timeout = 30000)]
    public async Task IdleTimeout_Disconnects()
    {
        using var server = SilentTcpServer.Start();
        var config = CreateConfig(server.Port);
        config.IdleTimeout = 300;
        await using var channel = new TcpClientChannel(config);
        var log = new StateLog(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

        StateEntry connected = await log.WaitForAsync(e => e.Current == ConnectionState.Connected, 5000);
        StateEntry lost = await log.WaitForAsync(e => e.Current == ConnectionState.Disconnected, 5000, connected);

        Assert.Equal(DisconnectReason.IdleTimeout, lost.Reason);
        Assert.InRange(lost.MillisecondsSince(connected), 240.0, 2300.0);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    [Fact(Timeout = 30000)]
    public async Task RequestTimeout_Sequential_ResetsConnection()
    {
        using var server = ScriptedTcpServer.Start(async (index, stream, token) =>
        {
            if (index == 0)
                await DrainAsync(stream, token);   // 第一个连接：读取但从不应答。
            else
                await EchoLinesAsync(stream, token);
        });

        var config = CreateConfig(server.Port);
        config.Framing = LineFraming();
        config.RequestTimeout = 300;
        config.Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 100 };
        await using var channel = new TcpClientChannel(config);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

        CommResult<byte[]> timedOut = await WithinAsync(channel.RequestAsync(Ascii("PING")), 5000);
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);

        // 超时后 Sequential 模式断开并重建：服务端应看到第二个连接，之后请求成功。
        await WaitUntilAsync(() => server.AcceptedConnectionCount == 2, 5000);
        Assert.True(await WithinAsync(channel.WaitForConnectedAsync(5000), 6000));

        CommResult<byte[]> reply = await WithinAsync(channel.RequestAsync(Ascii("PING")), 5000);
        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("PING"), DataOf(reply));
    }

    [Fact(Timeout = 30000)]
    public async Task Keyed_ConcurrentRequests_OverRealTcp()
    {
        const int count = 20;
        const int frameLength = 7;   // "REQ-NN\n"：每个请求都是 7 字节，服务端可以按长度一次读齐。
        using var server = ScriptedTcpServer.Start(async (index, stream, token) =>
        {
            var buffer = new byte[count * frameLength];
            if (!await ScriptedTcpServer.ReadExactAsync(stream, buffer, buffer.Length))
                return;

            // 读齐全部请求之后按相反的顺序应答：客户端必须按关联键匹配，而不是按到达顺序。
            string text = Encoding.ASCII.GetString(buffer);
            var reply = new StringBuilder();
            for (int i = count - 1; i >= 0; i--)
                reply.Append("ACK-").Append(text, i * frameLength + 4, 2).Append('\n');

            byte[] wire = Encoding.ASCII.GetBytes(reply.ToString());
            await stream.WriteAsync(wire, 0, wire.Length, token);
            await stream.FlushAsync(token);
        });

        var config = CreateConfig(server.Port);
        config.Framing = LineFraming();
        config.Correlation = CorrelationMode.Keyed;
        config.RequestTimeout = 5000;
        var components = new TcpChannelComponents { KeyExtractor = new TwoDigitKeyExtractor() };
        await using var channel = new TcpClientChannel(config, null, components);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

        Task<CommResult<byte[]>>[] requests = Enumerable.Range(0, count)
            .Select(i => channel.RequestAsync(Ascii($"REQ-{i:D2}")))
            .ToArray();
        CommResult<byte[]>[] replies = await WithinAsync(Task.WhenAll(requests), 10000);

        for (int i = 0; i < count; i++)
        {
            Assert.True(replies[i].IsSuccess, $"Request {i}: {replies[i]}");
            Assert.Equal(Ascii($"ACK-{i:D2}"), DataOf(replies[i]));
        }
    }

    [Fact(Timeout = 30000)]
    public async Task SendTimeout_ServerNotReading()
    {
        // 服务端的接收缓冲区为 4096 字节且从不读取；发送方的缓冲区也只有 4096 字节。
        // 实测（Windows 回环）：内核会接受第一次超大写入的全部数据（与接收缓冲区大小无关，256 MB 也立即写完），第二次写入才阻塞。
        // 因此连续写入 10 MB，直到某一次超时（最多 3 次）：在不接受超大写入的平台上第一次就超时；两种情况下都应得到 Timeout。
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = LoopbackServer.Start((index, client, token) =>
        {
            // 在被接受的套接字上设置接收缓冲区（监听套接字上的设置没有可靠地传给被接受的连接）。
            client.ReceiveBufferSize = 4096;
            accepted.TrySetResult(true);
            return Task.CompletedTask;
        });
        var config = CreateConfig(server.Port);
        config.SendTimeout = 500;
        config.Socket = new TcpSocketOptions { SendBufferSize = 4096 };
        await using var channel = new TcpClientChannel(config);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);
        Assert.True(await WithinAsync(accepted.Task, 5000));

        byte[] payload = new byte[10 * 1024 * 1024];
        CommResult sent = CommResult.Success();
        var stopwatch = new Stopwatch();
        for (int attempt = 1; attempt <= 3 && sent.IsSuccess; attempt++)
        {
            stopwatch.Restart();
            sent = await channel.SendAsync(payload);
            stopwatch.Stop();
            output.WriteLine($"Write {attempt}: result {sent}; elapsed {stopwatch.ElapsedMilliseconds} ms; state {channel.State}.");
        }

        Assert.Equal(CommErrorKind.Timeout, sent.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 400.0, 2500.0);
        await WaitUntilAsync(() => channel.State != ConnectionState.Connected, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task Disconnect_NoAutoReconnect_SendFailsFast()
    {
        using var server = ScriptedTcpServer.Start((index, stream, token) => DrainAsync(stream, token));
        var config = CreateConfig(server.Port);
        config.Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 100 };
        await using var channel = new TcpClientChannel(config);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => server.AcceptedConnectionCount == 1, 5000);

        await WithinAsync(channel.DisconnectAsync(), 5000);
        Assert.Equal(ConnectionState.Disconnected, channel.State);

        // 重连已开启，但用户断开之后不会被悄悄连回。
        await Task.Delay(2000);
        Assert.Equal(1, server.AcceptedConnectionCount);

        CommResult? sent = null;
        await TimingAssert.WithinAsync(TimeSpan.Zero, TimeSpan.FromMilliseconds(2000),
            async () => { sent = await channel.SendAsync(Ascii("PING")); });

        Assert.NotNull(sent);
        Assert.Equal(CommErrorKind.NotConnected, sent!.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task Dispose_DuringRequest_Loop20()
    {
        using var server = ScriptedTcpServer.Start((index, stream, token) => DrainAsync(stream, token));
        for (int i = 0; i < 20; i++)
        {
            var config = CreateConfig(server.Port);
            config.RequestTimeout = 10000;
            var channel = new TcpClientChannel(config);
            Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

            int received = server.ReceivedBytes;
            Task<CommResult<byte[]>> pending = channel.RequestAsync(Ascii("PING"));
            await WaitUntilAsync(() => server.ReceivedBytes >= received + 4, 5000);

            var stopwatch = Stopwatch.StartNew();
            channel.Dispose();
            Task winner = await Task.WhenAny(pending, Task.Delay(3000));
            stopwatch.Stop();

            Assert.Same(pending, winner);
            Assert.True(stopwatch.ElapsedMilliseconds < 3000, $"Iteration {i}: the request returned after {stopwatch.ElapsedMilliseconds} ms.");
            CommResult<byte[]> result = await pending;
            Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);

            int expectedClosed = i + 1;
            await WaitUntilAsync(() => server.ClosedByPeerCount >= expectedClosed, 5000);
        }

        Assert.True(server.ClosedByPeerCount >= 20);
    }

    // 两位十进制关联键：请求与应答的第 5、6 个字节（"REQ-NN" / "ACK-NN"）。
    private sealed class TwoDigitKeyExtractor : IFrameKeyExtractor
    {
        public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key) => TryReadKey(request, out key);

        public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key) => TryReadKey(frame, out key);

        private static bool TryReadKey(ReadOnlySpan<byte> bytes, out long key)
        {
            key = 0;
            if (bytes.Length < 6)
                return false;

            int tens = bytes[4] - '0';
            int ones = bytes[5] - '0';
            if (tens < 0 || tens > 9 || ones < 0 || ones > 9)
                return false;

            key = tens * 10 + ones;
            return true;
        }
    }
}
