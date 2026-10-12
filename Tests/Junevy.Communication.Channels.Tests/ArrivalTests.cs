using Junevy.Communication.Testing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 共享库 <see cref="Arrival"/> 的契约（计划 Task 2）：时限内完成就返回结果（操作自身的异常原样抛出），
/// 超时抛 <see cref="TimeoutException"/>。
/// </summary>
/// <remarks>这些用例不使用真实套接字，因此不属于 <c>SocketTiming</c> 集合，可以并行运行。</remarks>
public sealed class ArrivalTests
{
    [Fact]
    public async Task WithinAsync_CompletesInTime_ReturnsTheResult()
    {
        Task<int> pending = Task.FromResult(42);

        Assert.Equal(42, await Arrival.WithinAsync(pending, 5000));
    }

    [Fact]
    public async Task WithinAsync_CompletesInTime_AwaitsTheOperation()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task waiting = Arrival.WithinAsync((Task)completed.Task, 5000);
        Assert.False(waiting.IsCompleted);

        completed.SetResult(true);
        await waiting;
    }

    [Fact]
    public async Task WithinAsync_ExceedingTheLimit_ThrowsTimeoutNamingTheLimit()
    {
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => Arrival.WithinAsync((Task)never.Task, 50));

        Assert.Equal("The awaited operation did not complete within 50 ms.", error.Message);
    }

    [Fact]
    public async Task WithinAsync_WhenTheOperationItselfFails_PropagatesThatException()
    {
        Task<int> pending = Task.FromException<int>(new InvalidOperationException("boom"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Arrival.WithinAsync(pending, 5000));

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task WithinAsync_NullTask_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Arrival.WithinAsync((Task)null!, 100));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Arrival.WithinAsync((Task<int>)null!, 100));
    }

    [Fact]
    public async Task WithinAsync_NegativeLimit_Throws()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Arrival.WithinAsync(Task.CompletedTask, -1));
}