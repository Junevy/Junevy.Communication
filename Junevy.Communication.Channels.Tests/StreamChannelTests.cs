using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 流通道（<see cref="StreamChannel"/>）测试。全部使用内存双工流（<see cref="DuplexStreamPair"/>），不使用套接字。
/// 计划 8.3 的测试名逐条对应；计划第 20 节审阅者补充的测试放在末尾。时间断言使用区间（计划第 1 节第 5 条）。
/// </summary>
public sealed class StreamChannelTests
{
    [Fact]
    public async Task Send_FrameArrivesAtPeer()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(), Delimiter());
        rig.Start();

        CommResult sent = await WithinAsync(rig.Channel.SendAsync(Ascii("PING"), CancellationToken.None), 2000);
        Assert.True(sent.IsSuccess, sent.ToString());

        var peer = new StreamPeer(pair.B, Delimiter());
        Assert.Equal(Ascii("PING"), await WithinAsync(peer.ReceiveFrameAsync(), 2000));
        Assert.Equal(1, rig.Statistics.FramesSent);
    }

    [Fact]
    public async Task Receive_UnsolicitedRaisesEvent()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(), Delimiter());
        rig.Start();

        var peer = new StreamPeer(pair.B, Delimiter());
        await WithinAsync(peer.SendFrameAsync(Ascii("EVT-1")), 2000);

        await WaitForCountAsync(rig.Sink, 1);
        Assert.Equal(Ascii("EVT-1"), rig.Sink.Frames[0]);
    }

    [Fact]
    public async Task ConcurrentSends_FramesNotInterleaved()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(), Delimiter());
        rig.Start();

        const int count = 100;
        var peer = new StreamPeer(pair.B, Delimiter());
        Task<CommResult>[] sends = Enumerable.Range(0, count)
            .Select(i => rig.Channel.SendAsync(Ascii($"FRAME-{i:D3}"), CancellationToken.None))
            .ToArray();

        CommResult[] results = await WithinAsync(Task.WhenAll(sends), 5000);
        Assert.All(results, result => Assert.True(result.IsSuccess, result.ToString()));

        var received = new List<string>(count);
        for (int i = 0; i < count; i++)
            received.Add(Encoding.ASCII.GetString(await WithinAsync(peer.ReceiveFrameAsync(), 2000)));

        string[] expected = Enumerable.Range(0, count).Select(i => $"FRAME-{i:D3}").OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, received.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Request_Sequential_RoundTrip()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        using var device = new CancellationTokenSource();
        Task deviceTask = new DeviceSimulator(Delimiter(), frame => frame).RunAsync(pair.B, device.Token);
        await using var rig = new StreamRig(pair, Settings(), Delimiter());
        rig.Start();

        CommResult<byte[]> reply = await WithinAsync(rig.Channel.RequestAsync(Ascii("PING"), null, null, CancellationToken.None), 2000);
        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("PING"), reply.Data);

        device.Cancel();
        await WithinAsync(deviceTask, 5000);
    }

    [Fact]
    public async Task Request_And_Unsolicited_Interleaved()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(correlation: CorrelationMode.Matcher), Delimiter());
        rig.Start();

        // 设备在应答前先发一帧主动上报：请求的 Matcher 只认领以 'R' 开头的应答，'E' 开头的上报进入事件。
        var peer = new StreamPeer(pair.B, Delimiter());
        Task script = Task.Run(async () =>
        {
            await peer.ReceiveFrameAsync();
            await peer.SendFrameAsync(Ascii("EVT-1"));
            await peer.SendFrameAsync(Ascii("RSP-1"));
        });

        var options = new RequestOptions { Matcher = new LeadingByteMatcher() };
        CommResult<byte[]> reply = await WithinAsync(rig.Channel.RequestAsync(Ascii("REQ"), options, null, CancellationToken.None), 2000);
        await WithinAsync(script, 2000);

        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("RSP-1"), reply.Data);
        await WaitForCountAsync(rig.Sink, 1);
        Assert.Equal(Ascii("EVT-1"), rig.Sink.Frames[0]);
    }

    [Fact]
    public async Task Request_Timeout_ResetCallsOnFault()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        StreamChannelSettings settings = Settings(requestTimeout: 200);
        settings.ResetOnRequestTimeout = true;
        await using var rig = new StreamRig(pair, settings, Delimiter());
        rig.Start();

        // 对端不应答。
        var stopwatch = Stopwatch.StartNew();
        CommResult<byte[]> result = await WithinAsync(rig.Channel.RequestAsync(Ascii("REQ"), null, null, CancellationToken.None), 3000);
        stopwatch.Stop();

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 160d, 2200d);
        Assert.Equal(new[] { DisconnectReason.RequestTimeout }, rig.Faults);

        // 连接已判定为故障：之后的请求立即以 ConnectionClosed 失败。
        CommResult<byte[]> later = await WithinAsync(rig.Channel.RequestAsync(Ascii("REQ"), null, null, CancellationToken.None), 1000);
        Assert.Equal(CommErrorKind.ConnectionClosed, later.ErrorKind);
        Assert.Equal(new[] { DisconnectReason.RequestTimeout }, rig.Faults);
    }

    [Fact]
    public async Task Request_Timeout_NoReset_LateReplyDropped()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        StreamChannelSettings settings = Settings(requestTimeout: 200);   // LateReplyWindow = -1，即等于请求超时（200 ms）
        await using var rig = new StreamRig(pair, settings, Delimiter());
        rig.Start();

        var peer = new StreamPeer(pair.B, Delimiter());
        Task script = Task.Run(async () =>
        {
            await peer.ReceiveFrameAsync();       // REQ-1：不立即应答
            await Task.Delay(300);
            await peer.SendFrameAsync(Ascii("LATE-1"));   // 超时后 100 ms，位于 200 ms 迟到窗口内
            await peer.ReceiveFrameAsync();       // REQ-2
            await peer.SendFrameAsync(Ascii("RSP-2"));
        });

        CommResult<byte[]> first = await WithinAsync(rig.Channel.RequestAsync(Ascii("REQ-1"), null, null, CancellationToken.None), 3000);
        Assert.Equal(CommErrorKind.Timeout, first.ErrorKind);

        CommResult<byte[]> second = await WithinAsync(rig.Channel.RequestAsync(Ascii("REQ-2"), null, null, CancellationToken.None), 3000);
        await WithinAsync(script, 3000);

        Assert.True(second.IsSuccess, second.ToString());
        Assert.Equal(Ascii("RSP-2"), second.Data);
        Assert.Empty(rig.Sink.Frames);
        Assert.Equal(1, rig.Statistics.FramesDropped);
        Assert.Empty(rig.Faults);
    }

    [Fact]
    public async Task IdleGap_FlushAfterGap()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(), IdleGap(gapMs: 50));
        rig.Start();

        var peer = new StreamPeer(pair.B, IdleGap(gapMs: 50));
        var stopwatch = Stopwatch.StartNew();
        await WithinAsync(peer.SendRawAsync(Ascii("ABCDE")), 2000);
        await WaitForCountAsync(rig.Sink, 1);
        stopwatch.Stop();

        Assert.Equal(Ascii("ABCDE"), rig.Sink.Frames[0]);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 40d, 350d);
    }

    [Fact]
    public async Task IdleGap_ContinuousBytes_NotSplit()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();

        // 静默阈值取 100 ms（计划为 50 ms）：字节间隔 10 ms，留出余量以免在负载较高的机器上误判为静默。
        await using var rig = new StreamRig(pair, Settings(), IdleGap(gapMs: 100));
        rig.Start();

        var peer = new StreamPeer(pair.B, IdleGap(gapMs: 100));
        for (int i = 0; i < 10; i++)
        {
            await WithinAsync(peer.SendRawAsync(new[] { (byte)('A' + i) }), 2000);
            await Task.Delay(10);
        }

        await WaitForCountAsync(rig.Sink, 1);
        await Task.Delay(300);

        Assert.Single(rig.Sink.Frames);
        Assert.Equal(Ascii("ABCDEFGHIJ"), rig.Sink.Frames[0]);
    }

    [Fact]
    public async Task PartialFrame_Disconnect()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        StreamChannelSettings settings = Settings();
        settings.PartialFrameTimeout = 200;
        settings.PartialFrameAction = PartialFrameAction.Disconnect;
        await using var rig = new StreamRig(pair, settings, LengthField());
        rig.Start();

        // 长度字段声明 10 字节负载（帧总长 12），只写 7 字节。
        var peer = new StreamPeer(pair.B, LengthField());
        var stopwatch = Stopwatch.StartNew();
        await WithinAsync(peer.SendRawAsync(new byte[] { 0x00, 0x0A, 1, 2, 3, 4, 5 }), 2000);
        await WaitUntilAsync(() => rig.Faults.Count == 1, 3000);
        stopwatch.Stop();

        Assert.Equal(new[] { DisconnectReason.PartialFrameTimeout }, rig.Faults);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 150d, 2500d);
        Assert.Empty(rig.Sink.Frames);
    }

    [Fact]
    public async Task PartialFrame_Discard_ContinuesReceiving()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        StreamChannelSettings settings = Settings();
        settings.PartialFrameTimeout = 200;
        settings.PartialFrameAction = PartialFrameAction.Discard;
        await using var rig = new StreamRig(pair, settings, LengthField());
        rig.Start();

        var peer = new StreamPeer(pair.B, LengthField());
        await WithinAsync(peer.SendRawAsync(new byte[] { 0x00, 0x0A, 1, 2, 3, 4, 5 }), 2000);
        await WaitUntilAsync(() => rig.Statistics.ProtocolErrors == 1, 3000);

        // 残余字节已丢弃：之后的完整帧能正常交出。
        await WithinAsync(peer.SendRawAsync(LengthFrame("0123456789")), 2000);
        await WaitForCountAsync(rig.Sink, 1);

        Assert.Equal(Ascii("0123456789"), rig.Sink.Frames[0]);
        Assert.Equal(1, rig.Statistics.ProtocolErrors);
        Assert.Empty(rig.Faults);
    }

    [Fact]
    public async Task DecodeError_Disconnect()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(), LengthField(maxFrameLength: 64));
        rig.Start();

        // 长度字段声明 256 字节负载，超过 MaxFrameLength（64）。
        var peer = new StreamPeer(pair.B, LengthField(maxFrameLength: 64));
        await WithinAsync(peer.SendRawAsync(new byte[] { 0x01, 0x00 }), 2000);
        await WaitUntilAsync(() => rig.Faults.Count == 1, 3000);

        Assert.Equal(new[] { DisconnectReason.ProtocolViolation }, rig.Faults);
        Assert.Equal(1, rig.Statistics.ProtocolErrors);
    }

    [Fact]
    public async Task RemoteClose_FaultsAndFailsPending()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(requestTimeout: 5000), Delimiter());
        rig.Start();

        Task<CommResult<byte[]>> pending = rig.Channel.RequestAsync(Ascii("REQ"), null, null, CancellationToken.None);
        await Task.Delay(50);   // 请求已写出，对端不应答
        pair.Abort();

        CommResult<byte[]> result = await WithinAsync(pending, 2000);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(new[] { DisconnectReason.RemoteClosed }, rig.Faults);
        Assert.Equal(0, rig.AbortCount);
    }

    [Fact]
    public async Task RemoteEndOfStream_FaultsWithRemoteClosed()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(requestTimeout: 5000), Delimiter());
        rig.Start();

        Task<CommResult<byte[]>> pending = rig.Channel.RequestAsync(Ascii("REQ"), null, null, CancellationToken.None);
        await Task.Delay(50);
        pair.B.Dispose();   // 对端正常关闭：读到流末尾

        CommResult<byte[]> result = await WithinAsync(pending, 2000);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(new[] { DisconnectReason.RemoteClosed }, rig.Faults);
    }

    [Fact]
    public async Task SendTimeout_PeerNotReading()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create(pauseWriterThreshold: 4096);
        StreamChannelSettings settings = Settings();
        settings.SendTimeout = 300;
        await using var rig = new StreamRig(pair, settings, Raw());
        rig.Start();

        // 对端不读取：写出在 4096 字节后挂起，300 ms 后超时。
        var payload = new byte[64 * 1024];
        var stopwatch = Stopwatch.StartNew();
        CommResult sent = await WithinAsync(rig.Channel.SendAsync(payload, CancellationToken.None), 5000);
        stopwatch.Stop();

        Assert.Equal(CommErrorKind.Timeout, sent.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 240d, 2300d);
        Assert.Equal(1, rig.AbortCount);
        Assert.Equal(new[] { DisconnectReason.SendFailed }, rig.Faults);
    }

    [Fact]
    public async Task CancelWhileWaitingSendLock_DoesNotFault()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create(pauseWriterThreshold: 4096);
        await using var rig = new StreamRig(pair, Settings(), Raw());
        rig.Start();

        // 第一次发送持有发送锁，阻塞于对端不读。
        var big = new byte[64 * 1024];
        new Random(7).NextBytes(big);
        Task<CommResult> first = rig.Channel.SendAsync(big, CancellationToken.None);
        await Task.Delay(100);

        // 第二次发送在等待发送锁时被取消：只返回 Cancelled，不中止传输、不报告故障（D10）。
        using var cancellation = new CancellationTokenSource();
        Task<CommResult> second = rig.Channel.SendAsync(Ascii("X"), cancellation.Token);
        cancellation.CancelAfter(100);
        CommResult secondResult = await WithinAsync(second, 2000);

        Assert.Equal(CommErrorKind.Cancelled, secondResult.ErrorKind);
        Assert.Empty(rig.Faults);
        Assert.Equal(0, rig.AbortCount);

        // 对端读取第一次发送的字节：第一次成功完成，通道仍可用。
        var peer = new StreamPeer(pair.B, Raw());
        byte[] received = await WithinAsync(peer.ReadExactAsync(big.Length), 5000);
        CommResult firstResult = await WithinAsync(first, 5000);
        Assert.True(firstResult.IsSuccess, firstResult.ToString());
        Assert.Equal(big, received);

        CommResult third = await WithinAsync(rig.Channel.SendAsync(Ascii("AFTER"), CancellationToken.None), 2000);
        Assert.True(third.IsSuccess, third.ToString());
        Assert.Equal(Ascii("AFTER"), await WithinAsync(peer.ReadExactAsync(5), 2000));
        Assert.Empty(rig.Faults);
    }

    [Fact]
    public async Task CancelDuringWrite_Faults()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create(pauseWriterThreshold: 4096);
        await using var rig = new StreamRig(pair, Settings(), Raw());
        rig.Start();

        using var cancellation = new CancellationTokenSource();
        Task<CommResult> sending = rig.Channel.SendAsync(new byte[64 * 1024], cancellation.Token);
        await Task.Delay(100);   // 写出阻塞于对端不读

        cancellation.Cancel();
        CommResult result = await WithinAsync(sending, 2000);

        // 写出期间取消：中止传输并报告 SendFailed（D10）。
        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Equal(1, rig.AbortCount);
        Assert.Equal(new[] { DisconnectReason.SendFailed }, rig.Faults);
    }

    [Fact]
    public async Task Backpressure_WaitMode_ProducerBlocks()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create(pauseWriterThreshold: 64 * 1024);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();
        await using var rig = new StreamRig(pair, Settings(), Delimiter(),
                                            BlockingRaise(entered, release, sink), queueCapacity: 4, fullMode: QueueFullMode.Wait);
        rig.Start();

        // 约 3 MB 的帧：超过 Pipe 的 1 MiB 阈值与对端管道的阈值之和，写入必然挂起。
        const int frameCount = 3000;
        var peer = new StreamPeer(pair.B, Delimiter());
        Task writer = Task.Run(async () =>
        {
            for (int i = 0; i < frameCount; i++)
                await peer.SendFrameAsync(Sequenced(i));
        });

        await WithinAsync(entered.Task, 5000);
        await Task.Delay(500);
        Assert.False(writer.IsCompleted, "The peer's writes must block while the device side is not drained.");

        // 放行处理器：挂起的写入完成，全部帧按顺序派发，不丢帧。
        release.TrySetResult(true);
        await WithinAsync(writer, 30000);
        await WaitUntilAsync(() => sink.Count == frameCount, 30000);

        IReadOnlyList<byte[]> frames = sink.Frames;
        for (int i = 0; i < frameCount; i++)
            Assert.StartsWith($"{i:D6}", Encoding.ASCII.GetString(frames[i]), StringComparison.Ordinal);

        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Stop_FailsPendingAndCompletesLoops()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(requestTimeout: 10000), Delimiter());
        rig.Start();

        Task<CommResult<byte[]>> pending = rig.Channel.RequestAsync(Ascii("REQ"), null, null, CancellationToken.None);
        await Task.Delay(50);

        await WithinAsync(rig.Channel.StopAsync(1000), 2000);

        CommResult<byte[]> result = await WithinAsync(pending, 2000);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);

        // 主动停止不报告 onFault，只中止一次传输。
        Assert.Empty(rig.Faults);
        Assert.Equal(1, rig.AbortCount);

        CommResult<byte[]> after = await WithinAsync(rig.Channel.RequestAsync(Ascii("LATE"), null, null, CancellationToken.None), 1000);
        Assert.Equal(CommErrorKind.ConnectionClosed, after.ErrorKind);
    }

    [Fact]
    public async Task Stop_WhileWriteBlocked_DoesNotReportFault()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create(pauseWriterThreshold: 4096);
        await using var rig = new StreamRig(pair, Settings(), Raw());
        rig.Start();

        // 写出阻塞于对端不读；停止时中止传输使写出失败。这是主动停止，写出失败不得报告 onFault。
        Task<CommResult> sending = rig.Channel.SendAsync(new byte[64 * 1024], CancellationToken.None);
        await Task.Delay(100);

        await WithinAsync(rig.Channel.StopAsync(1000), 2000);

        CommResult result = await WithinAsync(sending, 2000);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Empty(rig.Faults);
        Assert.Equal(1, rig.AbortCount);
    }

    [Fact]
    public async Task EmptyPayloadRequest_ReturnsInvalidRequest()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        await using var rig = new StreamRig(pair, Settings(), Delimiter());
        rig.Start();

        // 空负载在路由表中表示 ReceiveAsync，因此在通道层直接拒绝，且不写出任何字节。
        CommResult<byte[]> result = await WithinAsync(rig.Channel.RequestAsync(ReadOnlyMemory<byte>.Empty, null, null, CancellationToken.None), 1000);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(0, rig.Statistics.FramesSent);

        CommResult sent = await WithinAsync(rig.Channel.SendAsync(Ascii("OK"), CancellationToken.None), 2000);
        Assert.True(sent.IsSuccess, sent.ToString());
    }

    [Fact]
    public async Task Sequential_TimeoutNoReset_HoldsLockDuringLateWindow()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        StreamChannelSettings settings = Settings(requestTimeout: 200);
        settings.LateReplyWindow = 400;
        await using var rig = new StreamRig(pair, settings, Delimiter());
        rig.Start();

        var clock = Stopwatch.StartNew();
        double requestBArrivedAt = -1;
        var peer = new StreamPeer(pair.B, Delimiter());
        Task script = Task.Run(async () =>
        {
            await peer.ReceiveFrameAsync();                   // REQ-A：不应答
            await Task.Delay(250);
            await peer.SendFrameAsync(Ascii("LATE-A"));       // A 超时后 50 ms，位于 400 ms 窗口内
            await peer.ReceiveFrameAsync();                   // REQ-B 应当在窗口结束后才写出
            requestBArrivedAt = clock.Elapsed.TotalMilliseconds;
            await peer.SendFrameAsync(Ascii("RSP-B"));
        });

        // B 在 A 的请求锁上等待；若 A 超时后立即释放锁，B 会在窗口内发出并认领 LATE-A。
        Task<CommResult<byte[]>> first = rig.Channel.RequestAsync(Ascii("REQ-A"), null, null, CancellationToken.None);
        Task<CommResult<byte[]>> second = rig.Channel.RequestAsync(Ascii("REQ-B"), null, null, CancellationToken.None);

        CommResult<byte[]> a = await WithinAsync(first, 3000);
        CommResult<byte[]> b = await WithinAsync(second, 5000);
        await WithinAsync(script, 3000);

        Assert.Equal(CommErrorKind.Timeout, a.ErrorKind);
        Assert.True(b.IsSuccess, b.ToString());
        Assert.Equal(Ascii("RSP-B"), b.Data);
        Assert.True(requestBArrivedAt >= 480d,
            $"REQ-B was written {requestBArrivedAt:F0} ms after REQ-A started, before the late reply window (ending near 600 ms) closed.");
        Assert.Empty(rig.Sink.Frames);
        Assert.Equal(1, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task StopFromFrameReceivedHandler_WithFullQueue_DoesNotDeadlock()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        double handlerElapsed = -1;
        StreamChannel? channel = null;

        // 第一帧的处理器等待测试放行后，在派发上下文中停止通道。此时队列容量为 1，解析循环挂起在满队列上。
        Func<FrameReceivedEventArgs, Task> raise = async args =>
        {
            await gate.Task;
            var stopwatch = Stopwatch.StartNew();
            await channel!.StopAsync(1000);
            handlerElapsed = stopwatch.Elapsed.TotalMilliseconds;
            handlerDone.TrySetResult(true);
        };

        await using var rig = new StreamRig(pair, Settings(), Delimiter(), raise, queueCapacity: 1, fullMode: QueueFullMode.Wait);
        channel = rig.Channel;
        rig.Start();

        var peer = new StreamPeer(pair.B, Delimiter());
        for (int i = 1; i <= 4; i++)
            await WithinAsync(peer.SendFrameAsync(Ascii($"F{i}")), 2000);

        // 第 3 帧已进入路由并等待队列空位：解析循环挂起。
        await WaitUntilAsync(() => rig.Statistics.FramesReceived >= 3, 3000);
        await Task.Delay(100);

        gate.TrySetResult(true);

        // 处理器应在 2 s 内返回（计划）；此处要求明显快于超时：停止本身只需毫秒级，若被迫等待 drainTimeout，耗时会超过 900 ms。
        await WithinAsync(handlerDone.Task, 2000);
        Assert.True(handlerElapsed < 900d, $"The stop called from the handler took {handlerElapsed:F0} ms; it must not wait for the drain timeout.");

        CommResult sent = await WithinAsync(rig.Channel.SendAsync(Ascii("AFTER"), CancellationToken.None), 1000);
        Assert.Equal(CommErrorKind.ConnectionClosed, sent.ErrorKind);
        Assert.Empty(rig.Faults);
    }

    [Fact]
    public async Task Stop_DrainsDispatchQueue()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();
        await using var rig = new StreamRig(pair, Settings(), Delimiter(), BlockingRaise(entered, release, sink));
        rig.Start();

        var peer = new StreamPeer(pair.B, Delimiter());
        for (int i = 1; i <= 4; i++)
            await WithinAsync(peer.SendFrameAsync(Ascii($"F{i}")), 2000);

        // F1 在处理器中阻塞，F2–F4 留在派发队列中。
        await WithinAsync(entered.Task, 2000);
        await WaitUntilAsync(() => rig.Statistics.FramesReceived == 4, 3000);
        await Task.Delay(100);

        Task stop = rig.Channel.StopAsync(2000);
        release.TrySetResult(true);
        await WithinAsync(stop, 3000);

        // 停止在队列排空之后才返回：4 帧全部派发，没有丢弃。
        IReadOnlyList<byte[]> frames = sink.Frames;
        Assert.Equal(4, frames.Count);
        for (int i = 1; i <= 4; i++)
            Assert.Equal(Ascii($"F{i}"), frames[i - 1]);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    // ————— 测试辅助 —————

    private static StreamChannelSettings Settings(int requestTimeout = 2000, CorrelationMode correlation = CorrelationMode.Sequential)
        => new StreamChannelSettings { RequestTimeout = requestTimeout, Correlation = correlation };

    private static IFrameCodecFactory Delimiter()
        => FrameCodecFactory.Create(new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } });

    private static IFrameCodecFactory Raw()
        => FrameCodecFactory.Create(new FramingOptions { Mode = FramingMode.Raw });

    private static IFrameCodecFactory IdleGap(int gapMs)
        => FrameCodecFactory.Create(new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = gapMs });

    private static IFrameCodecFactory LengthField(int maxFrameLength = 65536)
        => FrameCodecFactory.Create(new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = 0,
            LengthFieldSize = 2,
            LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
            InitialBytesToStrip = 2,
            MaxFrameLength = maxFrameLength,
        });

    // 两字节大端长度头 + 负载（与 LengthField 夹具一致）。
    private static byte[] LengthFrame(string payload)
    {
        byte[] body = Ascii(payload);
        return new byte[] { (byte)(body.Length >> 8), (byte)body.Length }.Concat(body).ToArray();
    }

    // 带序号的负载：6 位序号 + 1000 字节填充，便于核对顺序。
    private static byte[] Sequenced(int index)
    {
        byte[] prefix = Ascii($"{index:D6}");
        byte[] payload = new byte[prefix.Length + 1000];
        Array.Copy(prefix, payload, prefix.Length);
        for (int i = prefix.Length; i < payload.Length; i++)
            payload[i] = (byte)'x';

        return payload;
    }

    private static Func<FrameReceivedEventArgs, Task> BlockingRaise(
        TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release, RecordingSink sink)
    {
        return async args =>
        {
            entered.TrySetResult(true);
            await release.Task;
            await sink.Handle(args);
        };
    }
}

/// <summary>
/// 流通道测试装配：内存双工流的 A 端承载 <see cref="StreamChannel"/>，并记录路由、关联表、统计、日志、故障与中止调用。
/// 释放时停止通道（不排空），不释放 <see cref="DuplexStreamPair"/>（由测试负责）。
/// </summary>
internal sealed class StreamRig : IAsyncDisposable
{
    private readonly object sync = new object();
    private readonly List<DisconnectReason> faults = new List<DisconnectReason>();
    private int abortCount;

    /// <summary>
    /// 创建装配（未启动）。
    /// </summary>
    /// <param name="pair">双工流对；通道使用 <see cref="DuplexStreamPair.A"/>。</param>
    /// <param name="settings">通道运行参数。</param>
    /// <param name="codec">分帧配置。</param>
    /// <param name="raise">派发回调；为 null 时使用 <see cref="Sink"/>。</param>
    /// <param name="queueCapacity">派发队列容量。</param>
    /// <param name="fullMode">队列满时的处理方式。</param>
    public StreamRig(DuplexStreamPair pair, StreamChannelSettings settings, IFrameCodecFactory codec,
                     Func<FrameReceivedEventArgs, Task>? raise = null, int queueCapacity = 1024, QueueFullMode fullMode = QueueFullMode.Wait)
    {
        Pair = pair;
        Logger = new TestLogger();
        Statistics = new ConnectionStatistics();
        Sink = new RecordingSink();
        Table = new PendingRequestTable(settings.Correlation, null, settings.LateReplyWindow, Logger);
        Router = new FrameRouter(Table, queueCapacity, fullMode, raise ?? Sink.Raise, Statistics, Logger);
        Channel = new StreamChannel(pair.A, settings, codec.CreateDecoder(), codec.CreateEncoder(), Table, Router,
                                    Statistics, Logger, OnFault, OnAbort);
    }

    /// <summary>双工流对。</summary>
    public DuplexStreamPair Pair { get; }

    /// <summary>被测通道。</summary>
    public StreamChannel Channel { get; }

    /// <summary>日志（断言警告与错误）。</summary>
    public TestLogger Logger { get; }

    /// <summary>连接统计。</summary>
    public ConnectionStatistics Statistics { get; }

    /// <summary>默认派发记录。</summary>
    public RecordingSink Sink { get; }

    /// <summary>关联表。</summary>
    public PendingRequestTable Table { get; }

    /// <summary>帧路由。</summary>
    public FrameRouter Router { get; }

    /// <summary>已报告的故障原因（按报告顺序）。</summary>
    public IReadOnlyList<DisconnectReason> Faults
    {
        get
        {
            lock (sync)
                return faults.ToList();
        }
    }

    /// <summary>中止传输（abortTransport）的调用次数。</summary>
    public int AbortCount => Volatile.Read(ref abortCount);

    /// <summary>启动通道（同时启动派发循环）。</summary>
    public void Start() => Channel.Start();

    /// <summary>停止通道（不排空）。等待的上限为 3 秒，避免测试清理时挂起。</summary>
    public async ValueTask DisposeAsync()
    {
        Task stop = Channel.StopAsync(0);
        await Task.WhenAny(stop, Task.Delay(3000));
    }

    private void OnFault(DisconnectReason reason, Exception? exception)
    {
        lock (sync)
            faults.Add(reason);
    }

    // 测试中的 abortTransport：计数并断开双工流（模拟销毁底层连接）。
    private void OnAbort()
    {
        Interlocked.Increment(ref abortCount);
        Pair.Abort();
    }
}

/// <summary>
/// 测试对端：通过双工流的另一端收发帧。读取经分帧器切帧，写入经编码器编码。
/// </summary>
internal sealed class StreamPeer
{
    private readonly Stream stream;
    private readonly IFrameDecoder decoder;
    private readonly IFrameEncoder encoder;
    private byte[] pending = new byte[4096];
    private int pendingCount;

    public StreamPeer(Stream stream, IFrameCodecFactory codec)
    {
        this.stream = stream;
        decoder = codec.CreateDecoder();
        encoder = codec.CreateEncoder();
    }

    /// <summary>读取下一帧（经分帧器）。</summary>
    public async Task<byte[]> ReceiveFrameAsync()
    {
        while (true)
        {
            var remaining = new ReadOnlySequence<byte>(pending, 0, pendingCount);
            if (decoder.TryDecode(ref remaining, out ReadOnlySequence<byte> frame))
            {
                byte[] data = frame.ToArray();
                Consume(pendingCount - (int)remaining.Length);
                return data;
            }

            await FillAsync();
        }
    }

    /// <summary>读取恰好 <paramref name="count"/> 字节（不经分帧器）。</summary>
    public async Task<byte[]> ReadExactAsync(int count)
    {
        while (pendingCount < count)
            await FillAsync();

        byte[] data = new byte[count];
        Buffer.BlockCopy(pending, 0, data, 0, count);
        Consume(count);
        return data;
    }

    /// <summary>编码并发送一个负载。</summary>
    public async Task SendFrameAsync(byte[] payload)
    {
        var output = new ByteArrayBufferWriter();
        encoder.Encode(payload, output);
        await SendRawAsync(output.ToArray());
    }

    /// <summary>发送原始字节（不编码）。</summary>
    public async Task SendRawAsync(byte[] wire)
    {
        await stream.WriteAsync(wire, 0, wire.Length);
        await stream.FlushAsync();
    }

    private async Task FillAsync()
    {
        if (pendingCount == pending.Length)
            Array.Resize(ref pending, pending.Length * 2);

        int read = await stream.ReadAsync(pending, pendingCount, pending.Length - pendingCount);
        if (read == 0)
            throw new EndOfStreamException("The peer stream reached end of stream.");

        pendingCount += read;
    }

    private void Consume(int count)
    {
        Buffer.BlockCopy(pending, count, pending, 0, pendingCount - count);
        pendingCount -= count;
    }
}
