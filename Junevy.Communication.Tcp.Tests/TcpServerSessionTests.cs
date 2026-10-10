using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Tcp.Tests.ServerTestHelpers;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 服务端会话的接入、握手、关闭原因、心跳、广播、请求与帧派发（计划 11.3）。
/// 时间相关断言使用区间：下限取期望值的 80%，上限取期望值加 2000 毫秒（计划第 1 节第 5 条）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TcpServerSessionTests
{
    [Fact(Timeout = 30000)]
    public async Task AcceptsMultipleClients_RaisesSessionConnected()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var clients = new List<TcpClientChannel>();
        for (int i = 0; i < 5; i++)
        {
            TcpClientChannel client = CreateClient(port);
            clients.Add(client);
            Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        }

        await WaitUntilAsync(() => recorder.ConnectedCount == 5, 5000);
        Assert.Equal(5, server.SessionCount);
        Assert.Equal(5, server.Sessions.Count);

        await DisposeAllAsync(clients);
    }

    [Fact(Timeout = 30000)]
    public async Task MaxSessions_RejectsExtra()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.MaxSessions = 2;
        await using var server = new TcpServer(config);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel first = CreateClient(port);
        await using TcpClientChannel second = CreateClient(port);
        Assert.True((await WithinAsync(first.ConnectAsync(), 10000)).IsSuccess);
        Assert.True((await WithinAsync(second.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 2, 5000);

        using TcpClient third = await ConnectRawAsync(port);
        Assert.True(await WaitForPeerCloseAsync(third, 1000), "The third connection was not closed by the server within 1 s.");

        Assert.Equal(2, recorder.ConnectedCount);
        Assert.Equal(2, server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Whitelist_RejectsOthers()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.AllowedRemoteAddresses = new[] { "127.0.0.2" };
        await using var server = new TcpServer(config);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        // 来自 127.0.0.1 的连接不在白名单内：立即被关闭。
        using TcpClient denied = await ConnectRawAsync(port);
        Assert.True(await WaitForPeerCloseAsync(denied, 1000), "A non-whitelisted connection was not closed within 1 s.");
        Assert.Equal(0, recorder.ConnectedCount);

        // 绑定 127.0.0.2 作为来源（Windows 支持 127.0.0.0/8 的任意回环地址）：白名单内的连接正常接入。
        await using TcpClientChannel allowed = CreateClient(port, localAddress: "127.0.0.2");
        Assert.True((await WithinAsync(allowed.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), recorder.Connected[0].RemoteEndPoint.Address);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectionFilter_RejectsConnection()
    {
        int port = FreePort();
        var components = new TcpChannelComponents { ConnectionFilter = new DelegateFilter(remote => false) };
        await using var server = new TcpServer(CreateServerConfig(port), null, components);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        using TcpClient rejected = await ConnectRawAsync(port);

        Assert.True(await WaitForPeerCloseAsync(rejected, 1000), "The filtered connection was not closed within 1 s.");
        Assert.Equal(0, recorder.ConnectedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task SessionHandshake_Timeout_ClosesWithoutEvent()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.SessionHandshakeTimeout = 400;
        var components = new TcpChannelComponents
        {
            Initializer = new DelegateInitializer(async (channel, token) =>
            {
                // 等待一个永远不会到达的帧：握手超时后服务端关闭会话。
                await channel.ReceiveAsync(null, token);
                return CommResult.Success();
            }),
        };
        await using var server = new TcpServer(config, null, components);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);

        await WaitUntilAsync(() => !client.IsConnected, 5000);
        await Task.Delay(300);

        Assert.Equal(0, recorder.ConnectedCount);
        Assert.Equal(0, recorder.ClosedCount);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public async Task SessionHandshake_ClientSendsImmediately_BacklogClaimed()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.SessionHandshakeTimeout = 5000;
        var claimed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var components = new TcpChannelComponents
        {
            Initializer = new DelegateInitializer(async (channel, token) =>
            {
                // 对端连上后立即发帧；初始化器稍后才开始等待，首帧必须进入握手积压（D8）。
                await Task.Delay(200, token);
                CommResult<byte[]> frame = await channel.ReceiveAsync(new RequestOptions { Timeout = 3000 }, token);
                if (frame.IsSuccess)
                    claimed.TrySetResult(frame.Data!);
                else
                    claimed.TrySetException(new InvalidOperationException(frame.ToString()));

                return frame.ToResult();
            }),
        };
        await using var server = new TcpServer(config, null, components);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await client.SendAsync(Ascii("HELLO"));

        byte[] frameData = await WithinAsync(claimed.Task, 5000);
        Assert.Equal(Ascii("HELLO"), frameData);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);
        await Task.Delay(200);

        Assert.Empty(recorder.Frames);   // 已被握手认领，不作为 FrameReceived 派发。
        Assert.Equal(1, server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public async Task SessionClosed_RemoteClose_Reason()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);

        var stopwatch = Stopwatch.StartNew();
        await WithinAsync(client.DisconnectAsync(), 5000);
        await WaitUntilAsync(() => recorder.ClosedCount == 1, 5000);
        ServerRecorder.ClosedRecord closed = recorder.Closed[0];
        stopwatch.Stop();

        Assert.Equal(DisconnectReason.RemoteClosed, closed.Reason);
        // 对端关闭后会话应在约 2 秒内被移除并派发事件（同步停止的回归测试：不能等待解析循环自身）。
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"The session was closed {stopwatch.ElapsedMilliseconds} ms after the peer closed.");
        Assert.Equal(0, server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public async Task SessionIdleTimeout_Closes()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.SessionIdleTimeout = 300;
        await using var server = new TcpServer(config);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);
        await WaitUntilAsync(() => recorder.ClosedCount == 1, 5000);

        ServerRecorder.ClosedRecord closed = recorder.Closed[0];
        Assert.Equal(DisconnectReason.IdleTimeout, closed.Reason);
        double elapsed = (closed.Timestamp - recorder.ConnectedTimestamps[0]) * 1000.0 / Stopwatch.Frequency;
        Assert.InRange(elapsed, 240.0, 2300.0);
    }

    [Fact(Timeout = 30000)]
    public async Task SessionHeartbeat_Fails_Closes()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.Heartbeat = new HeartbeatOptions
        {
            Enabled = true,
            Interval = 200,
            Timeout = 200,
            MaxFailures = 2,
            Payload = "PING",
            ExpectedReply = "PONG",
        };
        await using var server = new TcpServer(config);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        // 客户端不回复心跳：服务端连续探测失败后关闭会话。
        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);

        await WaitUntilAsync(() => recorder.ClosedCount == 1, 10000);
        Assert.Equal(DisconnectReason.HeartbeatFailed, recorder.Closed[0].Reason);
    }

    [Fact(Timeout = 30000)]
    public async Task Broadcast_ToTenSessions()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var received = new int[10];
        var clients = new List<TcpClientChannel>();
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            TcpClientChannel client = CreateClient(port);
            client.FrameReceived += (sender, args) => Interlocked.Increment(ref received[index]);
            clients.Add(client);
            Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        }

        await WaitUntilAsync(() => recorder.ConnectedCount == 10, 5000);

        int delivered = await WithinAsync(server.BroadcastAsync(Ascii("BROADCAST")), 5000);

        Assert.Equal(10, delivered);
        await WaitUntilAsync(() => Enumerable.Range(0, 10).All(index => Volatile.Read(ref received[index]) == 1), 5000);
        await Task.Delay(200);
        Assert.All(Enumerable.Range(0, 10), index => Assert.Equal(1, Volatile.Read(ref received[index])));

        await DisposeAllAsync(clients);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerToSession_Request()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        // 客户端把收到的每一帧原样回显，用作服务端请求的应答。
        await using TcpClientChannel client = CreateClient(port);
        client.FrameReceived += (sender, args) => client.SendAsync(args.Data);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);

        ITcpSession session = recorder.Connected[0];
        CommResult<byte[]> reply = await WithinAsync(session.RequestAsync(Ascii("REQ-1"), new RequestOptions { Timeout = 3000 }), 5000);

        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("REQ-1"), DataOf(reply));
    }

    [Fact(Timeout = 30000)]
    public async Task FrameReceived_SessionThenServer()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);

        await client.SendAsync(Ascii("HELLO"));

        await WaitUntilAsync(() => recorder.Frames.Count == 2, 5000);
        Assert.Equal(new[] { "session:HELLO", "server:HELLO" }, recorder.Frames);
    }

    [Fact(Timeout = 30000)]
    public async Task HealthProbeFactory_CalledPerSessionWithThatSession()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 1000, MaxFailures = 3 };
        var created = new List<ITcpSession>();
        var components = new TcpChannelComponents
        {
            HealthProbeFactory = channel =>
            {
                // 服务端的工厂参数就是该会话（ITcpSession）。
                ITcpSession session = Assert.IsAssignableFrom<ITcpSession>(channel);
                lock (created)
                    created.Add(session);

                return new DelegateProbe(_ => Task.FromResult(CommResult.Success()));
            },
        };
        await using var server = new TcpServer(config, null, components);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var clients = new List<TcpClientChannel>();
        for (int i = 0; i < 3; i++)
        {
            TcpClientChannel client = CreateClient(port);
            clients.Add(client);
            Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        }

        await WaitUntilAsync(() => recorder.ConnectedCount == 3, 5000);
        await WaitUntilAsync(() =>
        {
            lock (created)
                return created.Count == 3;
        }, 5000);

        List<ITcpSession> calls;
        lock (created)
            calls = created.ToList();

        // 每个会话恰好调用一次，且参数就是该会话本身。
        Assert.Equal(3, calls.Select(session => session.Id).Distinct().Count());
        Assert.All(calls, session => Assert.Contains(recorder.Connected, connected => ReferenceEquals(connected, session)));
        await DisposeAllAsync(clients);
    }

    // 工厂抛出或返回 null 时，只关闭该会话，原因为 Error；其他会话不受影响。
    [Fact(Timeout = 30000)]
    public async Task HealthProbeFactory_ThrowsOrReturnsNull_ClosesThatSessionWithError()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 200, MaxFailures = 2 };
        int calls = 0;
        var components = new TcpChannelComponents
        {
            HealthProbeFactory = _ =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 1)
                    throw new InvalidOperationException("The probe factory failed.");

                if (call == 2)
                    return null!;

                return new DelegateProbe(_ => Task.FromResult(CommResult.Success()));
            },
        };
        await using var server = new TcpServer(config, null, components);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var clients = new List<TcpClientChannel>();
        for (int i = 0; i < 3; i++)
        {
            TcpClientChannel client = CreateClient(port);
            clients.Add(client);
            Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        }

        await WaitUntilAsync(() => recorder.ClosedCount == 2, 10000);
        Assert.All(recorder.Closed, closed => Assert.Equal(DisconnectReason.Error, closed.Reason));

        // 第三个会话的工厂返回了正常的探测：它保持连接。
        await Task.Delay(500);
        Assert.Equal(1, server.SessionCount);
        await DisposeAllAsync(clients);
    }

    [Fact(Timeout = 30000)]
    public async Task HealthProbeFactory_ProbeFails_ClosesThatSessionOnly()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 200, MaxFailures = 2 };
        var firstSession = new TaskCompletionSource<ITcpSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var components = new TcpChannelComponents
        {
            HealthProbeFactory = channel =>
            {
                // 只有第一个调用工厂的会话得到失败的探测，其余会话的探测健康。
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstSession.TrySetResult(Assert.IsAssignableFrom<ITcpSession>(channel));
                    return new DelegateProbe(_ => Task.FromResult(CommResult.Fail("The probe reports the session as unhealthy.", CommErrorKind.ProtocolViolation)));
                }

                return new DelegateProbe(_ => Task.FromResult(CommResult.Success()));
            },
        };
        await using var server = new TcpServer(config, null, components);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var clients = new List<TcpClientChannel>();
        for (int i = 0; i < 3; i++)
        {
            TcpClientChannel client = CreateClient(port);
            clients.Add(client);
            Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        }

        await WaitUntilAsync(() => recorder.ConnectedCount == 3, 5000);
        ITcpSession unhealthy = await WithinAsync(firstSession.Task, 5000);
        await WaitUntilAsync(() => recorder.ClosedCount == 1, 10000);

        ServerRecorder.ClosedRecord closed = recorder.Closed[0];
        Assert.Same(unhealthy, closed.Session);
        Assert.Equal(DisconnectReason.HeartbeatFailed, closed.Reason);

        // 其他会话的探测健康：在数个探测周期之后仍然连接。
        await Task.Delay(1000);
        Assert.Equal(1, recorder.ClosedCount);
        Assert.Equal(2, server.SessionCount);
        Assert.All(recorder.Connected.Where(session => !ReferenceEquals(session, unhealthy)), session => Assert.True(session.IsConnected));
        await DisposeAllAsync(clients);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerRequestTimeout_ResetTrue_ClosesSession()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        // 客户端不应答：服务端的请求超时后关闭该会话（ResetOnRequestTimeout 默认为 true）。
        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);

        ITcpSession session = recorder.Connected[0];
        CommResult<byte[]> reply = await WithinAsync(session.RequestAsync(Ascii("REQ"), new RequestOptions { Timeout = 200 }), 5000);

        Assert.Equal(CommErrorKind.Timeout, reply.ErrorKind);
        await WaitUntilAsync(() => recorder.ClosedCount == 1, 5000);
        Assert.Equal(DisconnectReason.RequestTimeout, recorder.Closed[0].Reason);
        Assert.False(session.IsConnected);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerRequestTimeout_ResetFalse_KeepsSessionAndDropsLateReply()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.ResetOnRequestTimeout = false;
        config.LateReplyWindow = 1000;
        await using var server = new TcpServer(config);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        // 客户端每收到一帧都在 500 毫秒后应答：第一个请求在 200 毫秒超时，其应答落在 1000 毫秒的迟到窗口内。
        await using TcpClientChannel client = CreateClient(port);
        client.FrameReceived += async (sender, args) =>
        {
            await Task.Delay(500);
            await client.SendAsync(Ascii("LATE"));
        };
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);

        ITcpSession session = recorder.Connected[0];
        CommResult<byte[]> first = await WithinAsync(session.RequestAsync(Ascii("REQ-1"), new RequestOptions { Timeout = 200 }), 10000);

        Assert.Equal(CommErrorKind.Timeout, first.ErrorKind);
        Assert.True(session.IsConnected, "A request timeout must not close the session when ResetOnRequestTimeout is false.");
        Assert.Equal(0, recorder.ClosedCount);
        Assert.Equal(1L, session.Statistics.FramesDropped);
        Assert.Empty(recorder.Frames);

        // 迟到窗口结束后连接仍可用：第二个请求拿到自己的应答。
        CommResult<byte[]> second = await WithinAsync(session.RequestAsync(Ascii("REQ-2"), new RequestOptions { Timeout = 3000 }), 10000);
        Assert.True(second.IsSuccess, second.ToString());
        Assert.Equal(Ascii("LATE"), DataOf(second));
    }

    private static async Task DisposeAllAsync(IEnumerable<IAsyncDisposable> items)
    {
        foreach (IAsyncDisposable item in items)
            await item.DisposeAsync();
    }
}
