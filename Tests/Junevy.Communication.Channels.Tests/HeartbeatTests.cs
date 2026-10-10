using System.Diagnostics;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 心跳与空闲监视测试（计划 9.4 的 <c>HeartbeatTests</c>）。时间断言使用区间（计划第 1 节第 5 条）。
/// </summary>
public sealed class HeartbeatTests
{
    [Fact(Timeout = 20000)]
    public async Task ProbeFailsMaxTimes_ReportsHeartbeatFailed()
    {
        var statistics = new ConnectionStatistics();
        var probe = new ScriptedProbe(_ => Task.FromResult(CommResult.Fail("The peer is down.", CommErrorKind.ConnectionClosed)));
        var deaths = new DeathRecorder();
        await using var monitor = NewMonitor(probe, interval: 100, maxFailures: 3, statistics, deaths.OnDead);
        monitor.Start();

        await WaitUntilAsync(() => deaths.Count >= 1, 3000);
        await Task.Delay(300);

        Assert.Equal(new[] { DisconnectReason.HeartbeatFailed }, deaths.Reasons);
        Assert.Equal(3, probe.Calls);
        Assert.Equal(3, statistics.ConsecutiveHeartbeatFailures);
    }

    [Fact(Timeout = 20000)]
    public async Task ProbeSuccess_ResetsCounter()
    {
        // 失败、失败、成功、失败、失败：连续失败的计数在成功后清零，因此不会达到 3 次。
        bool[] succeeded = { false, false, true, false, false };
        var statistics = new ConnectionStatistics();
        var probe = new ScriptedProbe(n => Task.FromResult(n <= succeeded.Length && !succeeded[n - 1]
            ? CommResult.Fail("A scripted failure.", CommErrorKind.ConnectionClosed)
            : CommResult.Success()));
        var deaths = new DeathRecorder();
        await using var monitor = NewMonitor(probe, interval: 50, maxFailures: 3, statistics, deaths.OnDead);
        monitor.Start();

        await WaitUntilAsync(() => probe.Calls >= 7, 3000);

        Assert.Equal(0, deaths.Count);
        Assert.Equal(0, statistics.ConsecutiveHeartbeatFailures);
    }

    [Fact(Timeout = 20000)]
    public async Task OnlyWhenIdle_SkipsWhenTraffic()
    {
        var statistics = new ConnectionStatistics();
        var probe = new ScriptedProbe(_ => Task.FromResult(CommResult.Success()));
        var deaths = new DeathRecorder();
        await using var monitor = NewMonitor(probe, interval: 200, maxFailures: 3, statistics, deaths.OnDead, onlyWhenIdle: true);
        monitor.Start();

        // 持续收发（每 5 ms 一帧，持续 1 秒）：探测间隔 200 ms 内始终有流量，探测不应被调用。
        var traffic = Stopwatch.StartNew();
        while (traffic.ElapsedMilliseconds < 1000)
        {
            statistics.RecordFrameReceived(1, DateTimeOffset.UtcNow);
            await Task.Delay(5);
        }

        Assert.Equal(0, probe.Calls);

        // 停止流量之后恢复探测。
        await WaitUntilAsync(() => probe.Calls >= 1, 3000);
    }

    [Fact(Timeout = 20000)]
    public async Task IdleTimeout_ReportsIdle()
    {
        var statistics = new ConnectionStatistics();
        var deaths = new DeathRecorder();
        await using var monitor = new HeartbeatMonitor(null, new HeartbeatOptions { Enabled = false }, 300, statistics, deaths.OnDead,
                                                       new TestLogger());
        var stopwatch = Stopwatch.StartNew();
        monitor.Start();

        await WaitUntilAsync(() => deaths.Count >= 1, 3000);
        double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

        Assert.Equal(new[] { DisconnectReason.IdleTimeout }, deaths.Reasons);
        Assert.InRange(elapsedMs, 240d, 2300d);
    }

    [Fact(Timeout = 20000)]
    public async Task PayloadProbe_ExpectedReply_ExactMatch()
    {
        var channel = new FakeByteChannel();
        var probe = new PayloadHeartbeatProbe(channel, Ascii("PING"), Ascii("OK"), 500);

        channel.NextReply = CommResult<byte[]>.Success(Ascii("OK"));
        CommResult same = await WithinAsync(probe.ProbeAsync(CancellationToken.None), 2000);
        Assert.True(same.IsSuccess, same.ToString());
        Assert.Equal(new[] { Ascii("PING") }, channel.Requests);
        Assert.Equal(500, channel.LastRequestTimeout);

        channel.NextReply = CommResult<byte[]>.Success(Ascii("OK!"));
        CommResult different = await WithinAsync(probe.ProbeAsync(CancellationToken.None), 2000);
        Assert.False(different.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, different.ErrorKind);

        channel.NextReply = CommResult<byte[]>.Fail("No reply.", CommErrorKind.Timeout);
        CommResult timedOut = await WithinAsync(probe.ProbeAsync(CancellationToken.None), 2000);
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);

        // 未指定期望应答：发送成功即健康。
        var sendOnly = new PayloadHeartbeatProbe(channel, Ascii("PING"), null, 500);
        CommResult sent = await WithinAsync(sendOnly.ProbeAsync(CancellationToken.None), 2000);
        Assert.True(sent.IsSuccess, sent.ToString());
        Assert.Equal(1, channel.SendCount);
    }

    [Fact(Timeout = 20000)]
    public async Task HangingProbe_TimesOut_ReportsHeartbeatFailed()
    {
        // 探测忽略取消令牌且永不返回：监视循环仍必须按超时判定失败，而不是无限等待。
        var statistics = new ConnectionStatistics();
        var probe = new ScriptedProbe(_ => new TaskCompletionSource<CommResult>().Task);
        var deaths = new DeathRecorder();
        await using var monitor = NewMonitor(probe, interval: 50, maxFailures: 2, statistics, deaths.OnDead, timeout: 100);
        monitor.Start();

        await WaitUntilAsync(() => deaths.Count >= 1, 3000);

        Assert.Equal(new[] { DisconnectReason.HeartbeatFailed }, deaths.Reasons);
        Assert.Equal(2, probe.Calls);
    }

    private static HeartbeatMonitor NewMonitor(IHealthProbe probe, int interval, int maxFailures, ConnectionStatistics statistics,
                                               Action<DisconnectReason> onDead, bool onlyWhenIdle = true, int timeout = 500)
    {
        var options = new HeartbeatOptions
        {
            Enabled = true,
            Interval = interval,
            Timeout = timeout,
            MaxFailures = maxFailures,
            OnlyWhenIdle = onlyWhenIdle,
        };

        return new HeartbeatMonitor(probe, options, 0, statistics, onDead, new TestLogger());
    }
}
