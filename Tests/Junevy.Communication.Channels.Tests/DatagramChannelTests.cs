using System.Diagnostics;
using System.Net;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// <see cref="DatagramChannel"/> 的确定性测试（计划 15.2 的通道级规则）：使用内存传输，不使用套接字。
/// 覆盖重发（同一个等待者）、迟到窗口、定向来源过滤、超长丢弃、Sequential 请求锁、发送失败与停止语义。
/// </summary>
public sealed class DatagramChannelTests
{
    private static readonly IPEndPoint PeerA = new IPEndPoint(IPAddress.Loopback, 7001);
    private static readonly IPEndPoint PeerB = new IPEndPoint(IPAddress.Loopback, 7002);
    private static readonly byte[] Request = { 0x01, 0x02 };
    private static readonly byte[] Reply = { 0xAA, 0xBB };

    [Fact(Timeout = 30000)]
    public async Task Request_ReturnsReplyFromDestination()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());
        rig.Transport.OnSend = s =>
        {
            rig.Transport.Deliver(Reply, s.Destination);
            return Task.CompletedTask;
        };

        CommResult<byte[]> result = await rig.Channel.RequestAsync(Request, null, PeerA, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Reply, result.Data);
        Assert.Single(rig.Transport.Sent);
        Assert.Equal(PeerA, rig.Transport.Sent[0].Destination);
    }

    [Fact(Timeout = 30000)]
    public async Task Request_RetryKeepsOneWaiter_ReplyToEarlierAttemptCountsAsSuccess()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings(s =>
        {
            s.RequestTimeout = 300;
            s.RequestRetryCount = 1;
        }));
        rig.Transport.OnSend = _ => Task.CompletedTask;

        Task<CommResult<byte[]>> request = rig.Channel.RequestAsync(Request, null, PeerA, CancellationToken.None);
        await Polling.WaitUntilAsync(() => rig.Transport.Sent.Count == 2);

        // 第二次发送之后，应答才到达（它是对第一次发送的应答）。等待者仍在表中，因此计为成功。
        rig.Transport.Deliver(Reply, PeerA);

        CommResult<byte[]> result = await request;
        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Reply, result.Data);
        Assert.Equal(2, rig.Transport.Sent.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task Request_RetriesExhausted_TimesOutAfterEveryAttempt()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings(s =>
        {
            s.RequestTimeout = 200;
            s.RequestRetryCount = 2;
        }));

        var watch = Stopwatch.StartNew();
        CommResult<byte[]> result = await rig.Channel.RequestAsync(Request, null, PeerA, CancellationToken.None);
        watch.Stop();

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Equal(3, rig.Transport.Sent.Count);
        Assert.InRange(watch.ElapsedMilliseconds, 480, 2600);
    }

    [Fact(Timeout = 30000)]
    public async Task LateReplyAfterRetriesExhausted_IsDroppedAndCounted()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings(s => s.RequestTimeout = 500));

        Task<CommResult<byte[]>> request = rig.Channel.RequestAsync(Request, null, PeerA, CancellationToken.None);
        await Polling.WaitUntilAsync(() => rig.Transport.Sent.Count == 1);

        // 超时（约 500 ms）之后、迟到窗口（同为 500 ms）之内到达的应答被丢弃。
        await Task.Delay(700);
        rig.Transport.Deliver(Reply, PeerA);

        CommResult<byte[]> result = await request;
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        await Polling.WaitUntilAsync(() => rig.Statistics.FramesDropped == 1);
        Assert.Equal(0, rig.FrameCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Request_EmptyPayload_ReturnsInvalidRequestWithoutSending()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());

        CommResult<byte[]> result = await rig.Channel.RequestAsync(ReadOnlyMemory<byte>.Empty, null, PeerA, CancellationToken.None);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(rig.Transport.Sent);
    }

    [Fact(Timeout = 30000)]
    public async Task Request_WithoutDestination_ReturnsInvalidRequest()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());

        CommResult<byte[]> result = await rig.Channel.RequestAsync(Request, null, null, CancellationToken.None);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(rig.Transport.Sent);
    }

    [Fact(Timeout = 30000)]
    public async Task Directed_OtherSource_IsDroppedAndCounted()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(PeerA), Settings());

        rig.Transport.Deliver(new byte[] { 1 }, PeerB);
        await Polling.WaitUntilAsync(() => rig.Statistics.FramesDropped == 1);
        Assert.Equal(0, rig.FrameCount);

        rig.Transport.Deliver(new byte[] { 2 }, PeerA);
        await Polling.WaitUntilAsync(() => rig.FrameCount == 1);
        Assert.Equal(0, rig.Statistics.ProtocolErrors);
    }

    [Fact(Timeout = 30000)]
    public async Task Oversize_IsDroppedAndCountedAsProtocolError()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings(s => s.MaxDatagramSize = 100));

        rig.Transport.Deliver(new byte[200], PeerA);
        await Polling.WaitUntilAsync(() => rig.Statistics.ProtocolErrors == 1);

        rig.Transport.Deliver(new byte[10], PeerA);
        await Polling.WaitUntilAsync(() => rig.FrameCount == 1);
        Assert.Equal(0, rig.Statistics.FramesDropped);
    }

    [Fact(Timeout = 30000)]
    public async Task SequentialRequests_HoldTheRequestLockThroughRetriesAndLateWindow()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings(s =>
        {
            s.RequestTimeout = 150;
            s.RequestRetryCount = 1;
        }));
        rig.Transport.OnSend = _ => Task.CompletedTask;

        Task<CommResult<byte[]>> first = rig.Channel.RequestAsync(Request, null, PeerA, CancellationToken.None);
        Task<CommResult<byte[]>> second = rig.Channel.RequestAsync(Request, null, PeerB, CancellationToken.None);
        await Task.WhenAll(first, second);

        // 第二个请求必须等第一个请求的全部重发与迟到窗口结束后才发出：第一个请求的第二次发送与第二个请求的首次发送之间至少相隔约 300 ms。
        Assert.Equal(4, rig.Transport.Sent.Count);
        Assert.Equal(PeerB, rig.Transport.Sent[2].Destination);
        double gapMs = (rig.Transport.Sent[2].SentAt - rig.Transport.Sent[1].SentAt).TotalMilliseconds;
        Assert.True(gapMs >= 240, $"The second request was sent {gapMs:F0} ms after the last attempt of the first one.");
    }

    [Fact(Timeout = 30000)]
    public async Task UserCancelledWhileWaiting_ReturnsCancelled_WithoutDroppingConnection()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings(s => s.RequestTimeout = 500));
        rig.Transport.OnSend = _ => Task.CompletedTask;
        using var cancellation = new CancellationTokenSource();

        Task<CommResult<byte[]>> request = rig.Channel.RequestAsync(Request, null, PeerA, cancellation.Token);
        await Polling.WaitUntilAsync(() => rig.Transport.Sent.Count == 1);
        cancellation.Cancel();

        CommResult<byte[]> result = await request;
        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Empty(rig.Faults);
    }

    [Fact(Timeout = 30000)]
    public async Task CancelDuringSend_ReturnsCancelled_AndReportsSendFailed()
    {
        // D10：用户取消正在写出的数据报会断开连接（可能已写出部分，连接不可信）。
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());
        using var cancellation = new CancellationTokenSource();
        rig.Transport.OnSend = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        CommResult<byte[]> result = await rig.Channel.RequestAsync(Request, null, PeerA, cancellation.Token);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        await Polling.WaitUntilAsync(() => rig.FaultCount == 1);
        Assert.Equal(DisconnectReason.SendFailed, rig.Faults[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task SendFailure_ReportsSendFailedOnce_AndAbortsTransport()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());
        rig.Transport.OnSend = _ => throw new IOException("The network is unreachable.");

        CommResult<byte[]> result = await rig.Channel.RequestAsync(Request, null, PeerA, CancellationToken.None);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);

        await Polling.WaitUntilAsync(() => rig.FaultCount == 1);
        Assert.Equal(DisconnectReason.SendFailed, rig.Faults[0]);
        Assert.Equal(1, rig.Transport.AbortCount);

        // 故障之后的发送立即失败，且不再报告第二次故障。
        CommResult sent = await rig.Channel.SendAsync(Request, PeerA, CancellationToken.None);
        Assert.Equal(CommErrorKind.ConnectionClosed, sent.ErrorKind);
        Assert.Equal(1, rig.FaultCount);
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveLoopFailure_ReportsErrorOnce()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());

        rig.Transport.FailReceive(new IOException("The link was lost."));

        await Polling.WaitUntilAsync(() => rig.FaultCount == 1);
        Assert.Equal(DisconnectReason.Error, rig.Faults[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task Stop_IsNotReportedAsFault_AndRepeatedStopsShareOneCompletion()
    {
        var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());

        Task first = rig.Channel.StopAsync(0);
        Task second = rig.Channel.StopAsync(0);
        await first;

        Assert.Same(first, second);
        Assert.Equal(0, rig.FaultCount);
        Assert.Equal(1, rig.Transport.AbortCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Send_RecordsFrameSentStatistics()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());

        CommResult sent = await rig.Channel.SendAsync(Request, PeerA, CancellationToken.None);

        Assert.True(sent.IsSuccess, sent.ToString());
        Assert.Equal(1, rig.Statistics.FramesSent);
        Assert.Equal(Request.Length, rig.Statistics.BytesSent);
    }

    [Fact(Timeout = 30000)]
    public async Task Receive_ReturnsNextUnclaimedDatagram()
    {
        await using var rig = new DatagramRig(new FakeDatagramTransport(null), Settings());

        Task<CommResult<byte[]>> receive = rig.Channel.ReceiveAsync(null, CancellationToken.None);
        rig.Transport.Deliver(Reply, PeerA);

        CommResult<byte[]> result = await receive;
        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Reply, result.Data);
    }

    private static DatagramChannelSettings Settings(Action<DatagramChannelSettings>? configure = null)
    {
        var settings = new DatagramChannelSettings
        {
            SendTimeout = 2000,
            RequestTimeout = 2000,
            RequestRetryCount = 0,
            LateReplyWindow = -1,
            Correlation = CorrelationMode.Sequential,
            MaxDatagramSize = 65507,
        };
        configure?.Invoke(settings);
        return settings;
    }

    // 组装一个数据报通道及其路由、统计，并记录派发的帧与故障。
    private sealed class DatagramRig : IAsyncDisposable
    {
        private readonly object sync = new object();
        private readonly List<FrameReceivedEventArgs> frames = new List<FrameReceivedEventArgs>();
        private readonly List<DisconnectReason> faults = new List<DisconnectReason>();

        public DatagramRig(FakeDatagramTransport transport, DatagramChannelSettings settings)
        {
            Transport = transport;
            Statistics = new ConnectionStatistics();
            ILogger logger = new TestLogger();
            Table = new PendingRequestTable(settings.Correlation, null, settings.LateReplyWindow, logger);
            Router = new FrameRouter(Table, 1024, QueueFullMode.DropOldest, RecordFrame, Statistics, logger);
            Channel = new DatagramChannel(transport, settings, Table, Router, Statistics, logger, RecordFault);
            Channel.Start();
        }

        public FakeDatagramTransport Transport { get; }

        public ConnectionStatistics Statistics { get; }

        public PendingRequestTable Table { get; }

        public FrameRouter Router { get; }

        public DatagramChannel Channel { get; }

        public int FrameCount
        {
            get
            {
                lock (sync)
                {
                    return frames.Count;
                }
            }
        }

        public int FaultCount
        {
            get
            {
                lock (sync)
                {
                    return faults.Count;
                }
            }
        }

        public IReadOnlyList<DisconnectReason> Faults
        {
            get
            {
                lock (sync)
                {
                    return faults.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync() => await Channel.StopAsync(0).ConfigureAwait(false);

        private Task RecordFrame(FrameReceivedEventArgs args)
        {
            lock (sync)
            {
                frames.Add(args);
            }

            return Task.CompletedTask;
        }

        private void RecordFault(DisconnectReason reason, Exception? exception)
        {
            lock (sync)
            {
                faults.Add(reason);
            }
        }
    }
}

/// <summary>轮询等待条件成立（测试用）。超时后断言失败。</summary>
internal static class Polling
{
    public static async Task WaitUntilAsync(Func<bool> condition, int milliseconds = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < milliseconds)
            await Task.Delay(5).ConfigureAwait(false);

        Assert.True(condition(), "The condition was not met within the time limit.");
    }
}
