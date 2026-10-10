using System.Diagnostics;
using System.Net;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 帧路由（<see cref="FrameRouter"/> / <see cref="PendingRequestTable"/> / <see cref="PendingRequest"/>）测试。全部为内存测试，不使用套接字。
/// 计划 7.3 的测试名与断言逐条对应；时间断言使用区间（下限 80%，上限期望 + 2000 ms）。
/// </summary>
public sealed class FrameRouterTests
{
    [Fact]
    public async Task Sequential_FirstFrameCompletesRequest()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 2000, null, CancellationToken.None);
        waiter.StartTimer();
        await rig.Router.RouteAsync(Ascii("RSP"), null, CancellationToken.None);

        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 2000);
        Assert.True(result.IsSuccess);
        Assert.Equal(Ascii("RSP"), result.Data);

        // 认领的帧不进入事件。
        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);
    }

    [Fact]
    public async Task Sequential_WithMatcher_NonMatchingGoesToEvent()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Ascii("R-request"), new LeadingByteMatcher(), 2000, null, CancellationToken.None);
        waiter.StartTimer();

        await rig.Router.RouteAsync(Ascii("X-unsolicited"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        Assert.False(waiter.Completion.IsCompleted, "A non-matching frame must not complete the request.");
        Assert.Equal(Ascii("X-unsolicited"), rig.Sink.Frames[0]);

        await rig.Router.RouteAsync(Ascii("R-reply"), null, CancellationToken.None);
        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 2000);
        Assert.True(result.IsSuccess);
        Assert.Equal(Ascii("R-reply"), result.Data);
        Assert.Equal(1, rig.Sink.Count);
    }

    [Fact]
    public async Task Matcher_OutOfOrderReplies_EachMatched()
    {
        await using var rig = new RouterRig(CorrelationMode.Matcher);
        rig.Start();

        var matcher = new LeadingByteMatcher();
        using PendingRequest first = rig.Table.Register(Tagged(1, "request"), matcher, 2000, null, CancellationToken.None);
        using PendingRequest second = rig.Table.Register(Tagged(2, "request"), matcher, 2000, null, CancellationToken.None);
        using PendingRequest third = rig.Table.Register(Tagged(3, "request"), matcher, 2000, null, CancellationToken.None);
        first.StartTimer();
        second.StartTimer();
        third.StartTimer();

        // 应答逆序到达。
        await rig.Router.RouteAsync(Tagged(3, "reply-3"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Tagged(1, "reply-1"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Tagged(2, "reply-2"), null, CancellationToken.None);

        CommResult<byte[]> r1 = await WithinAsync(first.Completion, 2000);
        CommResult<byte[]> r2 = await WithinAsync(second.Completion, 2000);
        CommResult<byte[]> r3 = await WithinAsync(third.Completion, 2000);
        Assert.Equal(Tagged(1, "reply-1"), r1.Data);
        Assert.Equal(Tagged(2, "reply-2"), r2.Data);
        Assert.Equal(Tagged(3, "reply-3"), r3.Data);

        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);
    }

    [Fact]
    public async Task Keyed_ConcurrentRequests_MatchedByKey()
    {
        await using var rig = new RouterRig(CorrelationMode.Keyed, keys: new PrefixKeyExtractor());
        rig.Start();

        const int count = 50;
        var waiters = new List<PendingRequest>();
        for (int key = 1; key <= count; key++)
        {
            PendingRequest waiter = rig.Table.Register(PrefixKeyExtractor.Frame(key, "request"), null, 5000, null, CancellationToken.None);
            waiter.StartTimer();
            waiters.Add(waiter);
        }

        // 随机顺序应答（固定种子，结果可复现）。
        var random = new Random(42);
        List<int> order = Enumerable.Range(1, count).OrderBy(_ => random.Next()).ToList();
        foreach (int key in order)
            await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(key, $"reply-{key}"), null, CancellationToken.None);

        for (int key = 1; key <= count; key++)
        {
            CommResult<byte[]> result = await WithinAsync(waiters[key - 1].Completion, 2000);
            Assert.True(result.IsSuccess, $"Request with key {key} did not complete successfully: {result}");
            Assert.Equal(PrefixKeyExtractor.Frame(key, $"reply-{key}"), result.Data);
        }

        foreach (PendingRequest waiter in waiters)
            waiter.Dispose();

        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);
    }

    [Fact]
    public async Task Keyed_MissingRequestKey_FailsInvalidRequest()
    {
        await using var rig = new RouterRig(CorrelationMode.Keyed, keys: new PrefixKeyExtractor());
        rig.Start();

        // 2 字节的请求无法提取 4 字节关联键：立即失败，且不登记到表中。
        using PendingRequest waiter = rig.Table.Register(Ascii("ab"), null, 2000, null, CancellationToken.None);
        Assert.True(waiter.Completion.IsCompleted);
        CommResult<byte[]> result = await waiter.Completion;
        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);

        // 未登记：此后的帧没有在途请求可认领，进入事件。
        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(1, "unsolicited"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        Assert.Equal(PrefixKeyExtractor.Frame(1, "unsolicited"), rig.Sink.Frames[0]);
    }

    [Fact]
    public async Task Keyed_DuplicateInFlightKey_FailsInvalidRequest()
    {
        await using var rig = new RouterRig(CorrelationMode.Keyed, keys: new PrefixKeyExtractor());
        rig.Start();

        using PendingRequest first = rig.Table.Register(PrefixKeyExtractor.Frame(7, "first"), null, 2000, null, CancellationToken.None);
        first.StartTimer();

        // 同一键已在途：第二个请求立即以 InvalidRequest 完成，第一个不受影响。
        using PendingRequest duplicate = rig.Table.Register(PrefixKeyExtractor.Frame(7, "duplicate"), null, 2000, null, CancellationToken.None);
        Assert.True(duplicate.Completion.IsCompleted);
        CommResult<byte[]> rejected = await duplicate.Completion;
        Assert.Equal(CommErrorKind.InvalidRequest, rejected.ErrorKind);
        Assert.False(first.Completion.IsCompleted);

        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(7, "reply"), null, CancellationToken.None);
        CommResult<byte[]> result = await WithinAsync(first.Completion, 2000);
        Assert.True(result.IsSuccess);
        Assert.Equal(PrefixKeyExtractor.Frame(7, "reply"), result.Data);
    }

    [Fact]
    public async Task ReceiveWaiter_ClaimsAfterRequest()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        using PendingRequest request = rig.Table.Register(Ascii("REQ"), null, 2000, null, CancellationToken.None);
        request.StartTimer();
        using PendingRequest receive = rig.Table.Register(ReadOnlyMemory<byte>.Empty, null, 2000, null, CancellationToken.None);

        // 第一帧给请求，第二帧给接收等待者。
        await rig.Router.RouteAsync(Ascii("first"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("second"), null, CancellationToken.None);

        CommResult<byte[]> requestResult = await WithinAsync(request.Completion, 2000);
        CommResult<byte[]> receiveResult = await WithinAsync(receive.Completion, 2000);
        Assert.Equal(Ascii("first"), requestResult.Data);
        Assert.Equal(Ascii("second"), receiveResult.Data);

        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);
    }

    [Fact]
    public async Task Timeout_CompletesWithTimeout()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 200, null, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();
        waiter.StartTimer();

        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 2000);
        stopwatch.Stop();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 160d, 2200d);
    }

    [Fact]
    public async Task TimerStartsOnlyAfterStartTimer()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 200, null, CancellationToken.None);

        // 注册后 300 ms 内没有开始计时（模拟请求帧仍在写出），因此不会超时。
        await Task.Delay(300);
        Assert.False(waiter.Completion.IsCompleted, "The timer must not run before StartTimer is called.");

        var stopwatch = Stopwatch.StartNew();
        waiter.StartTimer();
        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 2000);
        stopwatch.Stop();

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 160d, 2200d);
    }

    [Fact]
    public async Task Sequential_LateReplyWithinWindow_Dropped()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential, lateReplyWindow: 1000);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 100, null, CancellationToken.None);
        waiter.StartTimer();
        CommResult<byte[]> timedOut = await WithinAsync(waiter.Completion, 2000);
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);

        // 窗口内到达的应答是迟到应答：不派发，计入 FramesDropped 并记 Warning。
        await rig.Router.RouteAsync(Ascii("late-reply"), null, CancellationToken.None);
        await Task.Delay(200);
        Assert.Equal(0, rig.Sink.Count);
        Assert.Equal(1, rig.Statistics.FramesDropped);
        Assert.NotEmpty(rig.Logger.GetEntries(LogLevel.Warning));
    }

    [Fact]
    public async Task Keyed_LateReplyForTimedOutKey_Dropped()
    {
        await using var rig = new RouterRig(CorrelationMode.Keyed, lateReplyWindow: 1000, keys: new PrefixKeyExtractor());
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(PrefixKeyExtractor.Frame(9, "request"), null, 100, null, CancellationToken.None);
        waiter.StartTimer();
        CommResult<byte[]> timedOut = await WithinAsync(waiter.Completion, 2000);
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);

        // 超时键的迟到应答被丢弃；其他键的未认领帧照常派发。
        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(9, "late"), null, CancellationToken.None);
        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(10, "unsolicited"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        await Task.Delay(100);

        Assert.Equal(1, rig.Sink.Count);
        Assert.Equal(PrefixKeyExtractor.Frame(10, "unsolicited"), rig.Sink.Frames[0]);
        Assert.Equal(1, rig.Statistics.FramesDropped);
        Assert.NotEmpty(rig.Logger.GetEntries(LogLevel.Warning));
    }

    [Fact]
    public async Task Matcher_LateReply_RaisedAsUnsolicited()
    {
        await using var rig = new RouterRig(CorrelationMode.Matcher, lateReplyWindow: 1000);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Tagged(5, "request"), new LeadingByteMatcher(), 100, null, CancellationToken.None);
        waiter.StartTimer();
        CommResult<byte[]> timedOut = await WithinAsync(waiter.Completion, 2000);
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);

        // Matcher 模式无法识别迟到应答：按未认领帧派发，不丢弃。
        await rig.Router.RouteAsync(Tagged(5, "late"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        Assert.Equal(Tagged(5, "late"), rig.Sink.Frames[0]);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task UnconnectedUdp_SourceMismatch_NotClaimed()
    {
        var expected = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5000);
        var other = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 5000);

        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 2000, expected, CancellationToken.None);
        waiter.StartTimer();

        // 来源不符的数据报不认领，进入事件。
        await rig.Router.RouteAsync(Ascii("from-other"), other, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        Assert.False(waiter.Completion.IsCompleted);

        await rig.Router.RouteAsync(Ascii("from-expected"), expected, CancellationToken.None);
        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 2000);
        Assert.True(result.IsSuccess);
        Assert.Equal(Ascii("from-expected"), result.Data);
        Assert.Equal(1, rig.Sink.Count);
    }

    [Fact]
    public async Task FailAll_CompletesAllWaiters()
    {
        await using var rig = new RouterRig(CorrelationMode.Matcher);
        rig.Start();

        var waiters = new List<PendingRequest>();
        for (byte id = 1; id <= 3; id++)
        {
            PendingRequest waiter = rig.Table.Register(Tagged(id, "request"), new LeadingByteMatcher(), 5000, null, CancellationToken.None);
            waiter.StartTimer();
            waiters.Add(waiter);
        }

        PendingRequest receive = rig.Table.Register(ReadOnlyMemory<byte>.Empty, null, 5000, null, CancellationToken.None);
        waiters.Add(receive);

        rig.Table.FailAll(CommResult.Fail("The connection was closed.", CommErrorKind.ConnectionClosed));

        foreach (PendingRequest waiter in waiters)
        {
            CommResult<byte[]> result = await WithinAsync(waiter.Completion, 1000);
            Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
            waiter.Dispose();
        }
    }

    [Fact]
    public async Task Handshake_BacklogClaimedByLaterReceive()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        // 对端连上后立即发帧：此时还没有等待者，帧进入积压。
        rig.Table.BeginHandshake();
        await rig.Router.RouteAsync(Ascii("early"), null, CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);

        // 初始化器随后注册 ReceiveAsync：先扫描积压，直接拿到该帧。
        using PendingRequest receive = rig.Table.Register(ReadOnlyMemory<byte>.Empty, null, 2000, null, CancellationToken.None);
        Assert.True(receive.Completion.IsCompleted);
        CommResult<byte[]> result = await receive.Completion;
        Assert.Equal(Ascii("early"), result.Data);

        // 积压已被认领，握手结束时没有剩余帧。
        Assert.Empty(rig.Table.EndHandshake());
        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);
    }

    [Fact]
    public async Task Handshake_EndReturnsRemainingInOrder()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        rig.Table.BeginHandshake();
        await rig.Router.RouteAsync(Ascii("frame-1"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("frame-2"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("frame-3"), null, CancellationToken.None);

        IReadOnlyList<(byte[] Frame, EndPoint? Remote)> remaining = rig.Table.EndHandshake();
        Assert.Equal(3, remaining.Count);
        Assert.Equal(Ascii("frame-1"), remaining[0].Frame);
        Assert.Equal(Ascii("frame-2"), remaining[1].Frame);
        Assert.Equal(Ascii("frame-3"), remaining[2].Frame);

        // 握手结束后，新帧直接进入派发队列。
        await rig.Router.RouteAsync(Ascii("frame-4"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        await Task.Delay(100);
        Assert.Equal(1, rig.Sink.Count);
        Assert.Equal(Ascii("frame-4"), rig.Sink.Frames[0]);
    }

    [Fact]
    public async Task Handshake_BacklogOverflow_Throws()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        rig.Table.BeginHandshake();
        for (int i = 0; i < PendingRequestTable.MaxHandshakeBacklog; i++)
            await rig.Router.RouteAsync(Ascii($"frame-{i}"), null, CancellationToken.None);

        // 第 65 帧：积压已满，RouteAsync 抛出 FrameDecodeException（D8）。
        await Assert.ThrowsAsync<FrameDecodeException>(
            () => rig.Router.RouteAsync(Ascii("overflow"), null, CancellationToken.None).AsTask());

        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);
        Assert.Equal(PendingRequestTable.MaxHandshakeBacklog, rig.Table.EndHandshake().Count);
    }

    [Fact]
    public async Task HandlerException_DoesNotStopDispatch()
    {
        var logger = new TestLogger();
        EventHandler<FrameReceivedEventArgs>? handlers = null;
        Func<FrameReceivedEventArgs, Task> raise = args =>
        {
            FrameRouter.RaiseToSubscribers(handlers, this, args, logger);
            return Task.CompletedTask;
        };

        await using var rig = new RouterRig(CorrelationMode.Sequential, raise: raise, logger: logger);
        var received = new List<byte[]>();
        var sync = new object();

        // 第一个订阅者每次都抛出异常；第二个订阅者必须照常收到每一帧。
        handlers += (sender, args) => throw new InvalidOperationException("subscriber failure");
        handlers += (sender, args) =>
        {
            lock (sync)
                received.Add(args.Data);
        };

        rig.Start();
        await rig.Router.RouteAsync(Ascii("one"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("two"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("three"), null, CancellationToken.None);

        await WaitUntilAsync(() =>
        {
            lock (sync)
                return received.Count == 3;
        }, 3000);

        lock (sync)
        {
            Assert.Equal(Ascii("one"), received[0]);
            Assert.Equal(Ascii("two"), received[1]);
            Assert.Equal(Ascii("three"), received[2]);
        }

        Assert.Equal(3, logger.GetEntries(LogLevel.Error).Count);
    }

    [Fact]
    public async Task QueueFull_Wait_BlocksRoute()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();

        await using var rig = new RouterRig(CorrelationMode.Sequential, capacity: 2, fullMode: QueueFullMode.Wait,
                                            raise: BlockingRaise(entered, release, sink));
        rig.Start();

        // 第一帧被派发循环取出，处理器阻塞；之后两帧占满容量为 2 的队列。
        await rig.Router.RouteAsync(Ascii("frame-1"), null, CancellationToken.None);
        await WithinAsync(entered.Task, 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-2"), null, CancellationToken.None).AsTask(), 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-3"), null, CancellationToken.None).AsTask(), 2000);

        // 队列已满：第四帧的 RouteAsync 必须挂起（背压）。
        Task blocked = rig.Router.RouteAsync(Ascii("frame-4"), null, CancellationToken.None).AsTask();
        Task finished = await Task.WhenAny(blocked, Task.Delay(300));
        Assert.False(ReferenceEquals(finished, blocked), "RouteAsync must wait while the queue is full.");

        // 放行处理器后，挂起的调用完成，全部帧按顺序派发，不丢帧。
        release.TrySetResult(true);
        await WithinAsync(blocked, 2000);
        await WaitUntilAsync(() => sink.Count == 4, 3000);

        IReadOnlyList<byte[]> frames = sink.Frames;
        Assert.Equal(Ascii("frame-1"), frames[0]);
        Assert.Equal(Ascii("frame-2"), frames[1]);
        Assert.Equal(Ascii("frame-3"), frames[2]);
        Assert.Equal(Ascii("frame-4"), frames[3]);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task QueueFull_DropOldest_CountsDrops()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();

        await using var rig = new RouterRig(CorrelationMode.Sequential, capacity: 2, fullMode: QueueFullMode.DropOldest,
                                            raise: BlockingRaise(entered, release, sink));
        rig.Start();

        await rig.Router.RouteAsync(Ascii("frame-1"), null, CancellationToken.None);
        await WithinAsync(entered.Task, 2000);

        // 队列 [2, 3]；帧 4 挤掉最旧的 2，帧 5 挤掉 3。RouteAsync 从不挂起。
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-2"), null, CancellationToken.None).AsTask(), 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-3"), null, CancellationToken.None).AsTask(), 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-4"), null, CancellationToken.None).AsTask(), 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-5"), null, CancellationToken.None).AsTask(), 2000);

        Assert.Equal(2, rig.Statistics.FramesDropped);

        release.TrySetResult(true);
        await WaitUntilAsync(() => sink.Count == 3, 3000);
        IReadOnlyList<byte[]> frames = sink.Frames;
        Assert.Equal(Ascii("frame-1"), frames[0]);
        Assert.Equal(Ascii("frame-4"), frames[1]);
        Assert.Equal(Ascii("frame-5"), frames[2]);
    }

    [Fact]
    public async Task SlowHandler_DoesNotDelayReply()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();

        await using var rig = new RouterRig(CorrelationMode.Sequential, raise: BlockingRaise(entered, release, sink));
        rig.Start();

        // 处理器阻塞（派发循环被占用）期间，请求的应答必须照常认领。
        await rig.Router.RouteAsync(Ascii("unsolicited"), null, CancellationToken.None);
        await WithinAsync(entered.Task, 2000);

        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 2000, null, CancellationToken.None);
        waiter.StartTimer();
        await rig.Router.RouteAsync(Ascii("RSP"), null, CancellationToken.None);

        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 100);
        Assert.True(result.IsSuccess);
        Assert.Equal(Ascii("RSP"), result.Data);

        release.TrySetResult(true);
    }

    [Fact]
    public async Task Completion_ClaimTimeoutRace_CompletesOnce()
    {
        // 窗口为 0：超时后到达的应答直接派发，因此"只完成一次"可以用派发次数核对。
        await using var rig = new RouterRig(CorrelationMode.Sequential, lateReplyWindow: 0);
        rig.Start();

        const int iterations = 100;
        var random = new Random(3);
        int successes = 0;
        int timeouts = 0;
        for (int i = 0; i < iterations; i++)
        {
            using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 1, null, CancellationToken.None);
            waiter.StartTimer();

            // 应答在 0–25 ms 之间随机到达，与 1 ms 超时竞争。
            int delay = random.Next(0, 25);
            Task routing = Task.Run(async () =>
            {
                await Task.Delay(delay);
                await rig.Router.RouteAsync(Ascii($"RSP-{i}"), null, CancellationToken.None);
            });

            CommResult<byte[]> result = await WithinAsync(waiter.Completion, 2000);
            await routing;
            if (result.IsSuccess)
            {
                successes++;
            }
            else
            {
                Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
                timeouts++;
            }
        }

        // 每次请求恰好以一种结果完成：应答被认领（不派发），或超时（应答随后派发）。
        await WaitUntilAsync(() => rig.Sink.Count == timeouts, 3000);
        await Task.Delay(100);
        Assert.Equal(timeouts, rig.Sink.Count);
        Assert.Equal(iterations, successes + timeouts);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Claim_WhenQueueFull_DoesNotBlock()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();

        await using var rig = new RouterRig(CorrelationMode.Sequential, capacity: 1, fullMode: QueueFullMode.Wait,
                                            raise: BlockingRaise(entered, release, sink));
        rig.Start();

        await rig.Router.RouteAsync(Ascii("frame-1"), null, CancellationToken.None);
        await WithinAsync(entered.Task, 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-2"), null, CancellationToken.None).AsTask(), 2000);

        // 队列已满且派发被阻塞：应答由解析线程直接认领，不经过队列，因此 RouteAsync 不挂起。
        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 2000, null, CancellationToken.None);
        waiter.StartTimer();
        await WithinAsync(rig.Router.RouteAsync(Ascii("RSP"), null, CancellationToken.None).AsTask(), 500);

        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 500);
        Assert.Equal(Ascii("RSP"), result.Data);

        release.TrySetResult(true);
        await WaitUntilAsync(() => sink.Count == 2, 3000);
    }

    [Fact]
    public async Task UserCancel_CompletesWithCancelled_AndLateWindowApplies()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential, lateReplyWindow: 1000);
        rig.Start();

        using var cancellation = new CancellationTokenSource();
        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 5000, null, cancellation.Token);
        waiter.StartTimer();
        cancellation.Cancel();

        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 1000);
        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);

        // 用户取消等待应答与超时一样进入迟到应答窗口（D10）。
        await rig.Router.RouteAsync(Ascii("late"), null, CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(0, rig.Sink.Count);
        Assert.Equal(1, rig.Statistics.FramesDropped);
    }

    [Fact]
    public void Sequential_SecondInFlight_Throws()
    {
        var table = new PendingRequestTable(CorrelationMode.Sequential, null, -1, new TestLogger());
        using PendingRequest first = table.Register(Ascii("A"), null, 2000, null, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() =>
        {
            table.Register(Ascii("B"), null, 2000, null, CancellationToken.None);
        });
        Assert.False(first.Completion.IsCompleted);
    }

    [Fact]
    public async Task Dispose_BeforeReply_ReleasesWaiter()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        // 例如发送失败：调用方释放等待者。它不再认领帧，等待方也不会永远挂起。
        PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 2000, null, CancellationToken.None);
        waiter.StartTimer();
        waiter.Dispose();

        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 1000);
        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);

        await rig.Router.RouteAsync(Ascii("after-release"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        Assert.Equal(Ascii("after-release"), rig.Sink.Frames[0]);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Receive_Timeout_CompletesWithTimeout()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        // 接收等待者在注册时即开始计时。
        using PendingRequest waiter = rig.Table.Register(ReadOnlyMemory<byte>.Empty, null, 150, null, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();
        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 2000);
        stopwatch.Stop();

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 120d, 2150d);
    }

    [Fact]
    public async Task QueueFull_DropNewest_CountsDrops()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();

        await using var rig = new RouterRig(CorrelationMode.Sequential, capacity: 2, fullMode: QueueFullMode.DropNewest,
                                            raise: BlockingRaise(entered, release, sink));
        rig.Start();

        await rig.Router.RouteAsync(Ascii("frame-1"), null, CancellationToken.None);
        await WithinAsync(entered.Task, 2000);

        // 队列 [2, 3]；帧 4、5 是新到的，被丢弃。
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-2"), null, CancellationToken.None).AsTask(), 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-3"), null, CancellationToken.None).AsTask(), 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-4"), null, CancellationToken.None).AsTask(), 2000);
        await WithinAsync(rig.Router.RouteAsync(Ascii("frame-5"), null, CancellationToken.None).AsTask(), 2000);

        Assert.Equal(2, rig.Statistics.FramesDropped);

        release.TrySetResult(true);
        await WaitUntilAsync(() => sink.Count == 3, 3000);
        IReadOnlyList<byte[]> frames = sink.Frames;
        Assert.Equal(Ascii("frame-1"), frames[0]);
        Assert.Equal(Ascii("frame-2"), frames[1]);
        Assert.Equal(Ascii("frame-3"), frames[2]);
    }

    [Fact]
    public async Task RaiseDelegateThrows_IsLogged_DispatchContinues()
    {
        var sink = new RecordingSink();
        int calls = 0;
        Func<FrameReceivedEventArgs, Task> raise = args =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("raise failure");

            return sink.Handle(args);
        };

        await using var rig = new RouterRig(CorrelationMode.Sequential, raise: raise);
        rig.Start();

        await rig.Router.RouteAsync(Ascii("first"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("second"), null, CancellationToken.None);

        await WaitForCountAsync(sink, 1);
        Assert.Equal(Ascii("second"), sink.Frames[0]);
        Assert.Single(rig.Logger.GetEntries(LogLevel.Error));
    }

    [Fact]
    public async Task Keyed_LateKeyRecords_CappedAt256()
    {
        await using var rig = new RouterRig(CorrelationMode.Keyed, lateReplyWindow: 60000, keys: new PrefixKeyExtractor());
        rig.Start();

        const int keyCount = PendingRequestTable.MaxLateKeys + 1;
        for (int key = 1; key <= keyCount; key++)
        {
            using PendingRequest waiter = rig.Table.Register(PrefixKeyExtractor.Frame(key, "request"), null, 1, null, CancellationToken.None);
            waiter.StartTimer();
            CommResult<byte[]> timedOut = await WithinAsync(waiter.Completion, 2000);
            Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);
        }

        // 第 257 个超时键挤掉了最早记录的键 1：键 1 的迟到应答不再丢弃（派发），键 257 仍被丢弃。
        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(1, "late"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 1);
        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(keyCount, "late"), null, CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(1, rig.Sink.Count);
        Assert.Equal(PrefixKeyExtractor.Frame(1, "late"), rig.Sink.Frames[0]);
        Assert.Equal(1, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Handshake_BacklogDispatchedAfterEnd()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        rig.Table.BeginHandshake();
        await rig.Router.RouteAsync(Ascii("backlog-1"), null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("backlog-2"), null, CancellationToken.None);
        await rig.Router.EndHandshakeAsync(CancellationToken.None);

        await rig.Router.RouteAsync(Ascii("after"), null, CancellationToken.None);
        await WaitForCountAsync(rig.Sink, 3);
        IReadOnlyList<byte[]> frames = rig.Sink.Frames;
        Assert.Equal(Ascii("backlog-1"), frames[0]);
        Assert.Equal(Ascii("backlog-2"), frames[1]);
        Assert.Equal(Ascii("after"), frames[2]);
    }

    [Fact]
    public async Task Stop_DrainsQueuedFramesWithinTimeout()
    {
        var sink = new RecordingSink();
        Func<FrameReceivedEventArgs, Task> slow = async args =>
        {
            await Task.Delay(50);
            await sink.Handle(args);
        };

        await using var rig = new RouterRig(CorrelationMode.Sequential, raise: slow);
        rig.Start();

        // 每帧派发约 50 ms，因此 5 帧大部分仍在队列中。
        for (int i = 1; i <= 5; i++)
            await rig.Router.RouteAsync(Ascii($"frame-{i}"), null, CancellationToken.None);

        // 排空窗口 2000 ms 足够：全部派发完毕后 StopAsync 才返回。
        await rig.Router.StopAsync(2000);

        Assert.Equal(5, sink.Count);
        for (int i = 1; i <= 5; i++)
            Assert.Equal(Ascii($"frame-{i}"), sink.Frames[i - 1]);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Stop_DrainTimeoutExpires_DropsRemaining()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();

        await using var rig = new RouterRig(CorrelationMode.Sequential, raise: BlockingRaise(entered, release, sink));
        rig.Start();

        try
        {
            // 第一帧使处理器阻塞；之后 5 帧在队列中等待派发。
            await rig.Router.RouteAsync(Ascii("blocker"), null, CancellationToken.None);
            await WithinAsync(entered.Task, 2000);
            for (int i = 1; i <= 5; i++)
                await rig.Router.RouteAsync(Ascii($"frame-{i}"), null, CancellationToken.None);

            // 排空窗口 200 ms 到期：StopAsync 在区间内返回，不等待仍被阻塞的处理器。
            var stopwatch = Stopwatch.StartNew();
            await WithinAsync(rig.Router.StopAsync(200).AsTask(), 3000);
            stopwatch.Stop();
            Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 160d, 2200d);

            // 队列中未派发的 5 帧计入 FramesDropped。
            Assert.Equal(5, rig.Statistics.FramesDropped);
        }
        finally
        {
            release.TrySetResult(true);
        }

        // 放行处理器：只有已在处理的第一帧会交付；排队中的帧不会再派发。
        await WaitUntilAsync(() => sink.Count == 1, 3000);
        await Task.Delay(200);
        Assert.Equal(1, sink.Count);
        Assert.Equal(Ascii("blocker"), sink.Frames[0]);
        Assert.Equal(5, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Stop_SecondCallWaitsForFirst()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink();

        await using var rig = new RouterRig(CorrelationMode.Sequential, raise: BlockingRaise(entered, release, sink));
        rig.Start();

        try
        {
            // 第一帧使处理器阻塞；之后 3 帧在队列中等待派发。
            await rig.Router.RouteAsync(Ascii("blocker"), null, CancellationToken.None);
            await WithinAsync(entered.Task, 2000);
            for (int i = 1; i <= 3; i++)
                await rig.Router.RouteAsync(Ascii($"frame-{i}"), null, CancellationToken.None);

            // 第一次停止：排空窗口 5000 ms。处理器仍阻塞，因此它正在排空。
            Task first = rig.Router.StopAsync(5000).AsTask();

            // 第二次停止：drainTimeout 为 0，但必须等待第一次完成，不能提前返回（D16）。
            Task second = rig.Router.StopAsync(0).AsTask();
            await Task.Delay(200);
            Assert.False(first.IsCompleted, "The first stop must still be draining while the handler is blocked.");
            Assert.False(second.IsCompleted, "A second stop must wait for the first stop to finish.");

            // 放行处理器：排空完成，两次停止都返回；队列中的帧全部派发，没有丢弃。
            release.TrySetResult(true);
            await WithinAsync(first, 3000);
            await WithinAsync(second, 3000);
            Assert.Equal(4, sink.Count);
            Assert.Equal(0, rig.Statistics.FramesDropped);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task RouteAfterStop_Throws()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();
        await rig.Router.StopAsync(0);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => rig.Router.RouteAsync(Ascii("late"), null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Sequential_LateReplyWithinWindow_NotClaimedByReceiveWaiter()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential, lateReplyWindow: 1000);
        rig.Start();

        using PendingRequest request = rig.Table.Register(Ascii("REQ"), null, 100, null, CancellationToken.None);
        request.StartTimer();
        CommResult<byte[]> timedOut = await WithinAsync(request.Completion, 2000);
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);

        // 窗口内：无 Matcher 的接收等待者不能认领迟到应答；迟到应答被丢弃。
        using PendingRequest receive = rig.Table.Register(ReadOnlyMemory<byte>.Empty, null, 5000, null, CancellationToken.None);
        await rig.Router.RouteAsync(Ascii("late-reply"), null, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(receive.Completion.IsCompleted, "A receive waiter must not claim a late reply.");
        Assert.Equal(1, rig.Statistics.FramesDropped);
        Assert.Equal(0, rig.Sink.Count);

        // 窗口结束后：同一个接收等待者认领之后到达的帧。
        await Task.Delay(1200);
        await rig.Router.RouteAsync(Ascii("next-frame"), null, CancellationToken.None);
        CommResult<byte[]> result = await WithinAsync(receive.Completion, 2000);
        Assert.True(result.IsSuccess);
        Assert.Equal(Ascii("next-frame"), result.Data);
        Assert.Equal(1, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Keyed_LateReply_NotClaimedByReceiveWaiter()
    {
        await using var rig = new RouterRig(CorrelationMode.Keyed, lateReplyWindow: 1000, keys: new PrefixKeyExtractor());
        rig.Start();

        using PendingRequest request = rig.Table.Register(PrefixKeyExtractor.Frame(9, "request"), null, 100, null, CancellationToken.None);
        request.StartTimer();
        CommResult<byte[]> timedOut = await WithinAsync(request.Completion, 2000);
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);

        // 窗口内：超时键的迟到应答不能被接收等待者认领，被丢弃。
        using PendingRequest receive = rig.Table.Register(ReadOnlyMemory<byte>.Empty, null, 5000, null, CancellationToken.None);
        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(9, "late"), null, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(receive.Completion.IsCompleted, "A receive waiter must not claim a late reply of a timed-out key.");
        Assert.Equal(1, rig.Statistics.FramesDropped);
        Assert.Equal(0, rig.Sink.Count);

        // 窗口结束后：同一个接收等待者认领该键之后到达的帧。
        await Task.Delay(1200);
        await rig.Router.RouteAsync(PrefixKeyExtractor.Frame(9, "after-window"), null, CancellationToken.None);
        CommResult<byte[]> result = await WithinAsync(receive.Completion, 2000);
        Assert.True(result.IsSuccess);
        Assert.Equal(PrefixKeyExtractor.Frame(9, "after-window"), result.Data);
        Assert.Equal(1, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Keyed_ReceiveWaiter_UsesMatcher()
    {
        await using var rig = new RouterRig(CorrelationMode.Keyed, keys: new PrefixKeyExtractor());
        rig.Start();

        // 接收等待者没有请求也没有键，只能靠 Matcher 筛选（例如 HSMS 被动端等待 Select.req）：Keyed 模式下同样生效。
        using PendingRequest receive = rig.Table.Register(ReadOnlyMemory<byte>.Empty, new TagMatcher(0x09), 5000, null, CancellationToken.None);

        // 先到的不匹配帧：不被认领，进入事件。
        await rig.Router.RouteAsync(Ascii("other-frame"), null, CancellationToken.None);
        Assert.False(receive.Completion.IsCompleted, "A keyed receive waiter must not claim a frame its matcher rejects.");
        await WaitForCountAsync(rig.Sink, 1);
        Assert.Equal(Ascii("other-frame"), rig.Sink.Frames[0]);

        // 随后到达的匹配帧：被等待者认领，不进入事件。
        await rig.Router.RouteAsync(Tagged(0x09, "select-req"), null, CancellationToken.None);
        CommResult<byte[]> result = await WithinAsync(receive.Completion, 2000);
        Assert.True(result.IsSuccess);
        Assert.Equal(Tagged(0x09, "select-req"), result.Data);

        await Task.Delay(100);
        Assert.Equal(1, rig.Sink.Count);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact]
    public async Task Completion_TryFinish_SucceedsExactlyOnce()
    {
        var table = new PendingRequestTable(CorrelationMode.Sequential, null, -1, new TestLogger());
        using PendingRequest waiter = table.Register(Ascii("REQ"), null, 0, null, CancellationToken.None);

        // 32 个线程同时尝试完成同一个等待者（超时、取消、认领之间的竞态的极端形式）。
        const int threads = 32;
        using var start = new ManualResetEventSlim(false);
        Task<bool>[] attempts = Enumerable.Range(0, threads).Select(i => Task.Run(() =>
        {
            start.Wait();
            return waiter.TryFinish(CommResult<byte[]>.Fail($"attempt {i}", CommErrorKind.Unspecified));
        })).ToArray();

        start.Set();
        bool[] outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(won => won));
        CommResult<byte[]> result = await WithinAsync(waiter.Completion, 1000);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Completion_ContinuationDoesNotRunInline_OnRouteThread()
    {
        await using var rig = new RouterRig(CorrelationMode.Sequential);
        rig.Start();

        using PendingRequest waiter = rig.Table.Register(Ascii("REQ"), null, 2000, null, CancellationToken.None);
        waiter.StartTimer();

        // ExecuteSynchronously 的续延若在 TrySetResult 中被内联执行，就会在 RouteAsync 的调用线程上先于 RouteAsync 返回而运行。
        int continuationRan = 0;
        Task continuation = waiter.Completion.ContinueWith(
            _ => Interlocked.Exchange(ref continuationRan, 1),
            TaskContinuationOptions.ExecuteSynchronously);

        ValueTask routing = rig.Router.RouteAsync(Ascii("RSP"), null, CancellationToken.None);
        bool ranInline = Volatile.Read(ref continuationRan) == 1;
        await routing;

        await WithinAsync(continuation, 2000);
        Assert.False(ranInline, "The completion continuation must not run inline on the routing thread.");
    }

    private static Func<FrameReceivedEventArgs, Task> BlockingRaise(
        TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release, RecordingSink sink)
    {
        return async args =>
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            await sink.Handle(args).ConfigureAwait(false);
        };
    }
}
