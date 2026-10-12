using System.Diagnostics;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 共享库 <see cref="Polling"/> 的契约（计划 Task 1、P46-D4）：条件一开始就成立时不进轮询、稍后成立时返回、
/// 时限内反复检查、时限用尽后**再判一次**才失败，以及两个参数守卫。
/// </summary>
/// <remarks>
/// 这些用例不使用真实套接字，因此不属于 <c>SocketTiming</c> 集合，可以并行运行。</remarks>
public sealed class PollingTests
{
    [Fact]
    public async Task AlreadyTrue_DoesNotEnterThePollingLoop()
    {
        int calls = 0;

        await Polling.WaitUntilAsync(() =>
        {
            calls++;
            return true;
        }, 5000);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TurnsTrueLater_Returns()
    {
        int calls = 0;

        await Polling.WaitUntilAsync(() => ++calls >= 3, 5000);

        Assert.True(calls >= 3);
    }

    [Fact]
    public async Task NeverTrue_ThrowsTimeoutNamingTheLimit()
    {
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(
            () => Polling.WaitUntilAsync(() => false, 120));

        Assert.Equal("The condition was not met within 120 ms.", error.Message);
    }

    [Fact]
    public async Task TurnsTrueOnlyAfterTheLimit_StillSucceeds()
    {
        // 条件在整个轮询期间都是假的，只在时限用尽之后才成立——共享库必须在超时前再判一次，否则这里会误报超时。
        // 条件与循环读的是同一个时钟，因此判定不依赖调度器与机器负载。
        Stopwatch clock = Stopwatch.StartNew();
        const int limitMs = 20;

        await Polling.WaitUntilAsync(() => clock.ElapsedMilliseconds >= limitMs, limitMs);
    }

    [Fact]
    public async Task ChecksTheConditionRepeatedlyUntilTheLimit()
    {
        // 时限内必须反复检查条件，而不是判一次就放弃、或干脆睡到时限结束再判一次。
        int calls = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => Polling.WaitUntilAsync(() =>
        {
            calls++;
            return false;
        }, 200));

        Assert.True(calls >= 3, $"expected the condition to be checked repeatedly, but it was checked {calls} time(s).");
    }

    [Fact]
    public async Task NullCondition_Throws()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => Polling.WaitUntilAsync(null!, 100));

    [Fact]
    public async Task NegativeLimit_Throws()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Polling.WaitUntilAsync(() => true, -1));
}