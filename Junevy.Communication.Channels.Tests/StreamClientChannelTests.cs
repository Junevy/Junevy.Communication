using System.Diagnostics;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 字节流客户端通道测试（计划 9.4 的 <c>StreamClientChannelTests</c>）。使用 <see cref="DuplexClientChannel"/>（内存双工流），不使用套接字。
/// 包含审阅者要求的三条处理器内重入测试（D16 边界），以及握手竞态、跨代次统计等补充测试。
/// </summary>
public sealed class StreamClientChannelTests
{
    [Fact(Timeout = 20000)]
    public async Task SendWhenDisconnected_ReturnsNotConnectedImmediately()
    {
        var channel = new DuplexClientChannel(Settings());
        await using var owned = channel;

        CommResult warmUp = await WithinAsync(channel.SendAsync(Ascii("WARM")), 2000);
        Assert.Equal(CommErrorKind.NotConnected, warmUp.ErrorKind);

        var stopwatch = Stopwatch.StartNew();
        CommResult result = await WithinAsync(channel.SendAsync(Ascii("PING")), 2000);
        stopwatch.Stop();

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Took {stopwatch.ElapsedMilliseconds} ms.");
        Assert.Equal(0, channel.OpenCount);
    }

    [Fact(Timeout = 20000)]
    public async Task Initializer_RunsBeforeConnected()
    {
        var states = new List<ConnectionState>();
        var observedDuringInitializer = new List<ConnectionState>();
        DuplexClientChannel? channel = null;
        var initializer = new DelegateInitializer(async (view, token) =>
        {
            observedDuringInitializer.Add(channel!.State);
            await Task.Delay(200, token);
            observedDuringInitializer.Add(channel!.State);
            return CommResult.Success();
        });
        channel = new DuplexClientChannel(Settings(), new ChannelComponents { Initializer = initializer });
        await using var owned = channel;
        channel.StateChanged += (sender, args) =>
        {
            lock (states)
                states.Add(args.CurrentState);
        };

        CommResult result = await WithinAsync(channel.ConnectAsync(), 3000);

        Assert.True(result.IsSuccess, result.ToString());
        await WaitUntilAsync(() =>
        {
            lock (states)
                return states.Count >= 2;
        }, 3000);
        lock (states)
        {
            Assert.Equal(new[] { ConnectionState.Connecting, ConnectionState.Connecting }, observedDuringInitializer);
            Assert.Equal(new[] { ConnectionState.Connecting, ConnectionState.Connected }, states);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Initializer_PeerSendsFirst_BacklogClaimed()
    {
        // 对端在连接建立的同时就发送了一帧；初始化器稍后才调用 ReceiveAsync，必须仍然拿到该帧（D8）。
        byte[]? claimed = null;
        var frameEvents = 0;
        var initializer = new DelegateInitializer(async (view, token) =>
        {
            CommResult<byte[]> frame = await view.ReceiveAsync(new RequestOptions { Timeout = 2000 }, token);
            if (!frame.IsSuccess)
                return frame.ToResult();

            claimed = frame.Data;
            return CommResult.Success();
        });
        var channel = new DuplexClientChannel(Settings(delimiter: true), new ChannelComponents { Initializer = initializer },
                                              pair =>
                                              {
                                                  byte[] wire = Ascii("READY\r\n");
                                                  return pair.B.WriteAsync(wire, 0, wire.Length);
                                              });
        await using var owned = channel;
        channel.FrameReceived += (sender, args) => Interlocked.Increment(ref frameEvents);

        CommResult result = await WithinAsync(channel.ConnectAsync(), 3000);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Ascii("READY"), claimed);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref frameEvents));
    }

    [Fact(Timeout = 20000)]
    public async Task Initializer_Timeout_ConnectFails()
    {
        var initializer = new DelegateInitializer(async (view, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return CommResult.Success();
        });
        var channel = new DuplexClientChannel(Settings(handshakeTimeout: 300), new ChannelComponents { Initializer = initializer });
        await using var owned = channel;

        var stopwatch = Stopwatch.StartNew();
        CommResult result = await WithinAsync(channel.ConnectAsync(), 4000);
        stopwatch.Stop();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 240d, 2300d);
        Assert.True(channel.AbortCount >= 1, "The stream must be aborted after a handshake timeout.");
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Equal(1, channel.OpenCount);
    }

    [Fact(Timeout = 20000)]
    public async Task Initializer_PeerClosesDuringHandshake_ConnectFails()
    {
        // 对端在握手期间断开：故障已经记录在连接上，握手完成时必须判定为失败，而不是把已断开的连接报告为 Connected。
        var initializer = new DelegateInitializer(async (view, token) =>
        {
            await Task.Delay(200, token);
            return CommResult.Success();
        });
        var channel = new DuplexClientChannel(Settings(), new ChannelComponents { Initializer = initializer },
                                              pair =>
                                              {
                                                  pair.B.Dispose();
                                                  return Task.CompletedTask;
                                              });
        await using var owned = channel;

        CommResult result = await WithinAsync(channel.ConnectAsync(), 4000);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    [Fact(Timeout = 20000)]
    public async Task Initializer_RerunsOnReconnect()
    {
        int initializations = 0;
        var initializer = new DelegateInitializer((view, token) =>
        {
            Interlocked.Increment(ref initializations);
            return Task.FromResult(CommResult.Success());
        });
        var channel = new DuplexClientChannel(Settings(reconnect: true), new ChannelComponents { Initializer = initializer });
        await using var owned = channel;
        await WithinAsync(channel.ConnectAsync(), 3000);
        Assert.Equal(1, Volatile.Read(ref initializations));

        // 对端关闭连接：通道应自动重连，并再次执行初始化器。
        channel.LatestPair!.B.Dispose();

        await WaitUntilAsync(() => channel.State == ConnectionState.Connected && channel.OpenCount == 2, 5000);
        Assert.Equal(2, Volatile.Read(ref initializations));
    }

    [Fact(Timeout = 20000)]
    public async Task Reconnect_KeepsStatisticsAcrossGenerations()
    {
        var channel = new DuplexClientChannel(Settings(delimiter: true, reconnect: true));
        await using var owned = channel;
        await WithinAsync(channel.ConnectAsync(), 3000);
        DateTimeOffset? firstConnectedSince = channel.Statistics.ConnectedSince;
        Assert.NotNull(firstConnectedSince);

        var firstPeer = new StreamPeer(channel.LatestPair!.B, Delimiter());
        await WithinAsync(firstPeer.SendFrameAsync(Ascii("FIRST")), 2000);
        await WaitUntilAsync(() => channel.Statistics.FramesReceived == 1, 3000);

        channel.LatestPair!.B.Dispose();
        await WaitUntilAsync(() => channel.State == ConnectionState.Connected && channel.OpenCount == 2, 5000);

        var secondPeer = new StreamPeer(channel.LatestPair!.B, Delimiter());
        await WithinAsync(secondPeer.SendFrameAsync(Ascii("SECOND")), 2000);
        await WaitUntilAsync(() => channel.Statistics.FramesReceived == 2, 3000);

        Assert.Equal(1, channel.Statistics.ReconnectCount);
        Assert.Equal(2, channel.Statistics.FramesReceived);
        Assert.True(channel.Statistics.ConnectedSince > firstConnectedSince, "ConnectedSince must be updated by the reconnect.");
    }

    [Fact(Timeout = 20000)]
    public void HeartbeatWithoutPayloadOrProbe_Throws()
    {
        var settings = new ClientChannelSettings
        {
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 500, MaxFailures = 3, Payload = null },
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new DuplexClientChannel(settings));
        Assert.Contains("Heartbeat.Payload is required when neither IHealthProbe nor HealthProbeFactory is supplied.", ex.Message);
    }

    [Fact(Timeout = 20000)]
    public void KeyedWithoutKeyExtractor_Throws()
    {
        var settings = Settings();
        settings.Correlation = CorrelationMode.Keyed;

        Assert.Throws<ArgumentException>(() => new DuplexClientChannel(settings));
    }

    [Fact(Timeout = 20000)]
    public async Task Dispose_DuringInFlightRequest_ReturnsConnectionClosed()
    {
        for (int round = 0; round < 20; round++)
        {
            var channel = new DuplexClientChannel(Settings(delimiter: true, requestTimeout: 10000));
            CommResult connected = await WithinAsync(channel.ConnectAsync(), 3000);
            Assert.True(connected.IsSuccess, connected.ToString());

            // 对端收到请求后不回复：请求一直在途，直到释放。
            var peer = new StreamPeer(channel.LatestPair!.B, Delimiter());
            Task<CommResult<byte[]>> request = channel.RequestAsync(Ascii("REQ"));
            Assert.Equal(Ascii("REQ"), await WithinAsync(peer.ReceiveFrameAsync(), 3000));

            channel.Dispose();

            CommResult<byte[]> outcome = await WithinAsync(request, 3000);
            Assert.Equal(CommErrorKind.ConnectionClosed, outcome.ErrorKind);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task AfterDispose_ConnectAsyncThrows()
    {
        var channel = new DuplexClientChannel(Settings());
        channel.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.ConnectAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(Ascii("PING")));
    }

    [Fact(Timeout = 20000)]
    public async Task DisposeFromStateChangedHandler_NoDeadlock()
    {
        // 同步 Dispose 在 StateChanged 处理器内被调用：不能等待状态派发任务（它正在执行这个处理器），也不能拖到等待超时。
        DuplexClientChannel? channel = null;
        var handlerFinished = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        channel = new DuplexClientChannel(Settings());
        await using var owned = channel;
        channel.StateChanged += (sender, args) =>
        {
            if (args.CurrentState != ConnectionState.Connected || Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            var stopwatch = Stopwatch.StartNew();
            channel!.Dispose();
            handlerFinished.TrySetResult(stopwatch.Elapsed);
        };

        CommResult result = await WithinAsync(channel.ConnectAsync(), 3000);
        Assert.True(result.IsSuccess, result.ToString());

        Task finished = await Task.WhenAny(handlerFinished.Task, Task.Delay(5000));
        Assert.Same(handlerFinished.Task, finished);
        Assert.True((await handlerFinished.Task).TotalMilliseconds < 800, "Dispose from a StateChanged handler waited for the dispatch task.");
        Assert.Equal(ConnectionState.Disposed, channel.State);
    }

    [Fact(Timeout = 20000)]
    public async Task DisposeFromFrameReceivedHandler_NoDeadlock()
    {
        // 同步 Dispose 在 FrameReceived 处理器内被调用：路由的派发上下文只发出停止信号，不能等待派发循环。
        DuplexClientChannel? channel = null;
        var handlerFinished = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        channel = new DuplexClientChannel(Settings(delimiter: true));
        await using var owned = channel;
        channel.FrameReceived += (sender, args) =>
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            var stopwatch = Stopwatch.StartNew();
            channel!.Dispose();
            handlerFinished.TrySetResult(stopwatch.Elapsed);
        };
        await WithinAsync(channel.ConnectAsync(), 3000);

        var peer = new StreamPeer(channel.LatestPair!.B, Delimiter());
        await WithinAsync(peer.SendFrameAsync(Ascii("TRIGGER")), 2000);

        Task finished = await Task.WhenAny(handlerFinished.Task, Task.Delay(5000));
        Assert.Same(handlerFinished.Task, finished);
        Assert.True((await handlerFinished.Task).TotalMilliseconds < 800, "Dispose from a FrameReceived handler waited for the dispatch loop.");
        Assert.Equal(ConnectionState.Disposed, channel.State);
    }

    [Fact(Timeout = 20000)]
    public async Task DisconnectFromFrameReceivedHandler_NoDeadlock()
    {
        // 同步 DisconnectAsync 在 FrameReceived 处理器内被调用：必须在有限时间内返回。
        DuplexClientChannel? channel = null;
        var handlerFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = 0;
        channel = new DuplexClientChannel(Settings(delimiter: true));
        await using var owned = channel;
        channel.FrameReceived += (sender, args) =>
        {
            if (Interlocked.Exchange(ref disconnected, 1) != 0)
                return;

            DisconnectOnDispatchThread(channel!);
            handlerFinished.TrySetResult(true);
        };
        await WithinAsync(channel.ConnectAsync(), 3000);

        var peer = new StreamPeer(channel.LatestPair!.B, Delimiter());
        await WithinAsync(peer.SendFrameAsync(Ascii("TRIGGER")), 2000);

        Task finished = await Task.WhenAny(handlerFinished.Task, Task.Delay(5000));
        Assert.Same(handlerFinished.Task, finished);
        await WaitUntilAsync(() => channel.State == ConnectionState.Disconnected, 3000);
    }

    [Fact(Timeout = 20000)]
    public async Task ConnectedChannel_SendAndRequest_RoundTrip()
    {
        var channel = new DuplexClientChannel(Settings(delimiter: true));
        await using var owned = channel;
        await WithinAsync(channel.ConnectAsync(), 3000);

        var peer = new StreamPeer(channel.LatestPair!.B, Delimiter());
        CommResult sent = await WithinAsync(channel.SendAsync(Ascii("PING")), 2000);
        Assert.True(sent.IsSuccess, sent.ToString());
        Assert.Equal(Ascii("PING"), await WithinAsync(peer.ReceiveFrameAsync(), 2000));

        Task<CommResult<byte[]>> request = channel.RequestAsync(Ascii("Q1"));
        Assert.Equal(Ascii("Q1"), await WithinAsync(peer.ReceiveFrameAsync(), 2000));
        await WithinAsync(peer.SendFrameAsync(Ascii("A1")), 2000);

        CommResult<byte[]> reply = await WithinAsync(request, 2000);
        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("A1"), reply.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task FrameReceived_RaisedWithChannelAsSender()
    {
        var channel = new DuplexClientChannel(Settings(delimiter: true));
        await using var owned = channel;
        object? sender = null;
        byte[]? data = null;
        var raised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.FrameReceived += (s, args) =>
        {
            sender = s;
            data = args.Data;
            raised.TrySetResult(true);
        };
        await WithinAsync(channel.ConnectAsync(), 3000);

        var peer = new StreamPeer(channel.LatestPair!.B, Delimiter());
        await WithinAsync(peer.SendFrameAsync(Ascii("EVT")), 2000);

        await WithinAsync(raised.Task, 3000);
        Assert.Same(channel, sender);
        Assert.Equal(Ascii("EVT"), data);
    }

    [Fact(Timeout = 20000)]
    public async Task Dispose_ThenDisconnect_ReturnsImmediately()
    {
        var channel = new DuplexClientChannel(Settings());
        channel.Dispose();

        Task disconnect = channel.DisconnectAsync();
        await WithinAsync(disconnect, 1000);
    }

    // 心跳探测的请求超时：探测期间发出的请求超时不触发 ResetOnRequestTimeout，第 MaxFailures 次失败才以 HeartbeatFailed 断开。
    [Fact(Timeout = 30000)]
    public async Task HeartbeatProbeTimeout_DoesNotResetConnection_UntilMaxFailures()
    {
        ClientChannelSettings settings = Settings();
        settings.LateReplyWindow = 0;   // 保持原默认值（迟到应答窗口关闭）：本用例验证心跳计数，不依赖迟到窗口。默认值已改为 -1，见 ClientChannelSettings。
        settings.ResetOnRequestTimeout = true;
        settings.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 200, Timeout = 200, MaxFailures = 3, Payload = "PING", ExpectedReply = "PONG" };
        await using var channel = new DuplexClientChannel(settings);   // 对端不回复心跳。
        TaskCompletionSource<ConnectionStateChangedEventArgs> lost = ObserveLoss(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 3000)).IsSuccess);

        await WaitUntilAsync(() => channel.Statistics.ConsecutiveHeartbeatFailures >= 2 || lost.Task.IsCompleted, 5000);
        Assert.False(lost.Task.IsCompleted, "The connection was lost before the second heartbeat failure.");
        Assert.Equal(ConnectionState.Connected, channel.State);

        ConnectionStateChangedEventArgs lostEvent = await WithinAsync(lost.Task, 10000);
        Assert.Equal(DisconnectReason.HeartbeatFailed, lostEvent.Reason);
        Assert.True(channel.Statistics.ConsecutiveHeartbeatFailures >= 3);
    }

    // 自定义探测经 RequestAsync 发出的请求与内置探测一样属于探测期间，超时不重建连接。
    [Fact(Timeout = 30000)]
    public async Task CustomProbeTimeout_DoesNotResetConnection()
    {
        DuplexClientChannel? channel = null;
        var probe = new DelegateProbe(async token =>
        {
            CommResult<byte[]> reply = await channel!.RequestAsync(Ascii("PING"), new RequestOptions { Timeout = 200 }, token);
            return reply.IsSuccess ? CommResult.Success() : reply.ToResult();
        });
        ClientChannelSettings settings = Settings();
        settings.LateReplyWindow = 0;   // 保持原默认值（迟到应答窗口关闭）：探测内的请求超时只计心跳失败，不占用请求锁。默认值已改为 -1，见 ClientChannelSettings。
        settings.ResetOnRequestTimeout = true;
        settings.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 200, Timeout = 200, MaxFailures = 3 };
        channel = new DuplexClientChannel(settings, new ChannelComponents { HealthProbe = probe });
        await using var owned = channel;
        TaskCompletionSource<ConnectionStateChangedEventArgs> lost = ObserveLoss(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 3000)).IsSuccess);

        await WaitUntilAsync(() => channel.Statistics.ConsecutiveHeartbeatFailures >= 2 || lost.Task.IsCompleted, 5000);
        Assert.False(lost.Task.IsCompleted, "The connection was lost before the second probe failure.");
        Assert.Equal(ConnectionState.Connected, channel.State);

        ConnectionStateChangedEventArgs lostEvent = await WithinAsync(lost.Task, 10000);
        Assert.Equal(DisconnectReason.HeartbeatFailed, lostEvent.Reason);
        Assert.True(probe.Calls >= 3);
    }

    // 探测上下文只属于探测自己的执行流：探测尚未返回时，用户请求的超时照常重建连接（修复不得外溢）。
    [Fact(Timeout = 30000)]
    public async Task UserRequestTimeout_WhileProbeIsOpen_StillResetsConnection()
    {
        var probe = new DelegateProbe(async token =>
        {
            // 探测一直未返回，直到通道释放（取消）为止。
            await Task.Delay(Timeout.Infinite, token);
            return CommResult.Fail("The probe was cancelled.", CommErrorKind.Cancelled);
        });
        ClientChannelSettings settings = Settings(requestTimeout: 300);
        settings.ResetOnRequestTimeout = true;
        settings.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 50, Timeout = 10000, MaxFailures = 3 };
        await using var channel = new DuplexClientChannel(settings, new ChannelComponents { HealthProbe = probe });
        TaskCompletionSource<ConnectionStateChangedEventArgs> lost = ObserveLoss(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 3000)).IsSuccess);
        await WaitUntilAsync(() => probe.Calls >= 1, 5000);   // 探测已经开始，且远未返回（超时 10 秒）。

        CommResult<byte[]> reply = await WithinAsync(channel.RequestAsync(Ascii("REQ")), 5000);

        Assert.Equal(CommErrorKind.Timeout, reply.ErrorKind);
        ConnectionStateChangedEventArgs lostEvent = await WithinAsync(lost.Task, 5000);
        Assert.Equal(DisconnectReason.RequestTimeout, lostEvent.Reason);
    }

    // ————— 测试辅助 —————

    private static ClientChannelSettings Settings(bool delimiter = false, int handshakeTimeout = 0, bool reconnect = false, int requestTimeout = 2000)
    {
        var settings = new ClientChannelSettings
        {
            HandshakeTimeout = handshakeTimeout,
            SendTimeout = 2000,
            RequestTimeout = requestTimeout,
            DisconnectTimeout = 0,
            Framing = delimiter
                ? new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } }
                : new FramingOptions { Mode = FramingMode.Raw },
        };

        if (reconnect)
        {
            settings.Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 50, MaxAttempts = 0 };
        }

        return settings;
    }

    private static IFrameCodecFactory Delimiter()
        => FrameCodecFactory.Create(new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } });

    // 在 FrameReceived 处理器的派发线程上同步等待断开：这正是要验证的重入场景，因此放在测试方法之外。
    private static void DisconnectOnDispatchThread(DuplexClientChannel channel)
        => channel.DisconnectAsync().GetAwaiter().GetResult();

    // 记录第一次离开 Connected 的状态变化（即连接丢失的事件）。
    private static TaskCompletionSource<ConnectionStateChangedEventArgs> ObserveLoss(DuplexClientChannel channel)
    {
        var lost = new TaskCompletionSource<ConnectionStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.StateChanged += (sender, e) =>
        {
            if (e.PreviousState == ConnectionState.Connected)
                lost.TrySetResult(e);
        };

        return lost;
    }

    // 委托实现的心跳探测（测试用），并统计调用次数。
    private sealed class DelegateProbe : IHealthProbe
    {
        private readonly Func<CancellationToken, Task<CommResult>> probe;
        private int calls;

        public DelegateProbe(Func<CancellationToken, Task<CommResult>> probe)
        {
            this.probe = probe;
        }

        public int Calls => Volatile.Read(ref calls);

        public Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return probe(cancellationToken);
        }
    }
}
