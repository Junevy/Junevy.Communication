using System.Net;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 心跳探测与请求锁的交互：探测排在请求锁上、超时时没有写出任何帧，这不是对端沉默的证据，不计为心跳失败。
/// 使用内存数据报传输，确定性地复现"用户请求的迟到窗口期内探测到期"的情形。
/// </summary>
public sealed class HeartbeatProbeBlockingTests
{
    private static readonly IPEndPoint Peer = new IPEndPoint(IPAddress.Loopback, 7001);

    [Fact(Timeout = 20000)]
    public async Task HeartbeatProbe_BlockedByRequestLock_IsNotCountedAsFailure()
    {
        // 用户请求超时后，Sequential 通道在迟到窗口（1500 ms）内持有请求锁。窗口期内到期的探测排在请求锁上，超时时没有写出任何帧。
        var statistics = new ConnectionStatistics();
        var transport = new FakeDatagramTransport(null);
        DatagramChannel channel = NewDatagramChannel(transport, statistics);
        var probe = new ChannelRequestProbe(channel);
        var deaths = new DeathRecorder();
        await using var monitor = new HeartbeatMonitor(probe, new HeartbeatOptions
        {
            Enabled = true,
            Interval = 50,
            Timeout = 100,
            MaxFailures = 2,
            OnlyWhenIdle = false,
        }, 0, statistics, deaths.OnDead, new TestLogger());

        Task<CommResult<byte[]>> userRequest = channel.RequestAsync(new byte[] { 1 }, null, Peer, CancellationToken.None);
        await WaitUntilAsync(() => transport.Sent.Count == 1, 3000);
        monitor.Start();

        // 1000 ms 时仍处于迟到窗口之内（窗口约为 300–1800 ms）：被阻塞的探测不得计为失败，因此不应判定死亡。
        await Task.Delay(1000);
        Assert.Equal(0, deaths.Count);
        Assert.Equal(0, statistics.ConsecutiveHeartbeatFailures);
        Assert.True(probe.Attempts >= 3, $"The probe should have run while the request lock was held; attempts: {probe.Attempts}.");

        // 迟到窗口结束、请求锁释放后，探测真正发出并被计为失败；连续两次即判定死亡。
        await WaitUntilAsync(() => deaths.Count >= 1, 8000);
        Assert.Equal(new[] { DisconnectReason.HeartbeatFailed }, deaths.Reasons);

        await monitor.StopAsync();
        await channel.StopAsync(0);
        await userRequest;
    }

    private static DatagramChannel NewDatagramChannel(FakeDatagramTransport transport, ConnectionStatistics statistics)
    {
        var settings = new DatagramChannelSettings
        {
            SendTimeout = 2000,
            RequestTimeout = 300,
            RequestRetryCount = 0,
            LateReplyWindow = 1500,
            Correlation = CorrelationMode.Sequential,
            MaxDatagramSize = 65507,
        };
        ILogger logger = new TestLogger();
        var table = new PendingRequestTable(settings.Correlation, null, settings.LateReplyWindow, logger);
        var router = new FrameRouter(table, 1024, QueueFullMode.DropOldest, _ => Task.CompletedTask, statistics, logger);
        var channel = new DatagramChannel(transport, settings, table, router, statistics, logger, (reason, exception) => { });
        channel.Start();
        return channel;
    }

    // 以数据报通道的请求作为探测：探测与用户请求共用同一把请求锁。
    private sealed class ChannelRequestProbe : IHealthProbe
    {
        private static readonly byte[] Ping = { 0x50, 0x49, 0x4E, 0x47 };
        private readonly DatagramChannel channel;
        private int attempts;

        public ChannelRequestProbe(DatagramChannel channel)
        {
            this.channel = channel;
        }

        public int Attempts => Volatile.Read(ref attempts);

        public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            CommResult<byte[]> reply = await channel.RequestAsync(Ping, new RequestOptions { Timeout = 100 }, Peer, cancellationToken)
                .ConfigureAwait(false);
            return reply.ToResult();
        }
    }
}
