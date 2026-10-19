using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Plc;

/// <summary>监控表的轮询（计划 Task 7 第 3 条、DA-D10）：上一轮未完成时跳过本轮、停止后不再开始新的一轮、刷新与进行中的一轮不重叠。</summary>
public sealed class PlcPollerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Rounds_RunRepeatedly_UntilStopped()
    {
        int rounds = 0;
        await using var poller = new PlcPoller((_, _) =>
        {
            Interlocked.Increment(ref rounds);
            return Task.CompletedTask;
        });

        poller.Start(new FakePlcClient(), intervalMs: 50);
        await Waiting.UntilAsync(() => Volatile.Read(ref rounds) >= 3, Wait);
        poller.Stop();
        await Task.Delay(150);
        int afterStop = Volatile.Read(ref rounds);
        await Task.Delay(200);

        Assert.Equal(afterStop, Volatile.Read(ref rounds));
    }

    [Fact]
    public async Task WhileARoundIsInProgress_TheRoundsDueMeanwhileAreSkipped()
    {
        var release = new TaskCompletionSource();
        int rounds = 0;
        await using var poller = new PlcPoller(async (_, _) =>
        {
            if (Interlocked.Increment(ref rounds) == 1)
            {
                await release.Task;
            }
        });

        poller.Start(new FakePlcClient(), intervalMs: 50);
        await Waiting.UntilAsync(() => Volatile.Read(ref rounds) == 1, Wait);
        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref rounds));

        release.SetResult();
        await Waiting.UntilAsync(() => Volatile.Read(ref rounds) >= 2, Wait);
        poller.Stop();
    }

    [Fact]
    public async Task Refresh_WaitsForTheRoundInProgress_AndDoesNotOverlapIt()
    {
        var release = new TaskCompletionSource();
        int running = 0;
        int maxRunning = 0;
        await using var poller = new PlcPoller(async (_, _) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            try
            {
                await release.Task;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        });

        poller.Start(new FakePlcClient(), intervalMs: 50);
        await Waiting.UntilAsync(() => Volatile.Read(ref running) == 1, Wait);
        Task refresh = poller.RefreshAsync(new FakePlcClient());
        await Task.Delay(200);
        Assert.False(refresh.IsCompleted, "刷新应当等待正在进行的一轮结束。");

        release.SetResult();
        await refresh.WaitAsync(Wait);
        poller.Stop();
        Assert.Equal(1, Volatile.Read(ref maxRunning));
    }

    [Fact]
    public async Task Start_WhileRunning_DoesNotStartASecondLoop()
    {
        int rounds = 0;
        await using var poller = new PlcPoller((_, _) =>
        {
            Interlocked.Increment(ref rounds);
            return Task.CompletedTask;
        });

        poller.Start(new FakePlcClient(), intervalMs: 50);
        poller.Start(new FakePlcClient(), intervalMs: 50);
        await Waiting.UntilAsync(() => Volatile.Read(ref rounds) >= 4, Wait);
        poller.Stop();
        await Task.Delay(150);
        int afterStop = Volatile.Read(ref rounds);
        await Task.Delay(200);

        // 第二次开始不应留下第二个循环：停止后轮数不再增加。
        Assert.Equal(afterStop, Volatile.Read(ref rounds));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
        }
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
