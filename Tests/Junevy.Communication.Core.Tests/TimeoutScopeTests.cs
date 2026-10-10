using System.Diagnostics;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="TimeoutScope"/> 的超时与取消判别测试。
/// </summary>
public sealed class TimeoutScopeTests
{
    [Fact]
    public async Task Timeout_SetsIsTimedOutAndCallsAbortOnce()
    {
        var stopwatch = Stopwatch.StartNew();
        var aborted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        int abortCount = 0;

        using var scope = TimeoutScope.Start(100, CancellationToken.None, () =>
        {
            Interlocked.Increment(ref abortCount);
            aborted.TrySetResult(stopwatch.Elapsed);
        });

        var completed = await Task.WhenAny(aborted.Task, Task.Delay(5000));
        Assert.Same(aborted.Task, completed);

        TimeSpan elapsed = await aborted.Task;
        Assert.True(scope.IsTimedOut);
        Assert.False(scope.IsUserCancelled);

        // 耗时区间：下限取期望值的 80%，上限取期望值 + 2000 ms。
        Assert.InRange(elapsed.TotalMilliseconds, 80d, 2100d);

        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref abortCount));
    }

    [Fact]
    public void UserCancel_SetsIsUserCancelled()
    {
        using var userSource = new CancellationTokenSource();
        int abortCount = 0;
        using var scope = TimeoutScope.Start(10000, userSource.Token, () => Interlocked.Increment(ref abortCount));

        userSource.Cancel();

        Assert.True(scope.IsUserCancelled);
        Assert.False(scope.IsTimedOut);
        Assert.Equal(1, abortCount);
    }

    [Fact]
    public async Task NonPositiveTimeout_NeverTimesOut()
    {
        using var scope = TimeoutScope.Start(0, CancellationToken.None);

        await Task.Delay(500);

        Assert.False(scope.Token.IsCancellationRequested);
        Assert.False(scope.IsTimedOut);
    }

    [Fact]
    public async Task Dispose_BeforeTimeout_DoesNotCallAbort()
    {
        int abortCount = 0;
        var scope = TimeoutScope.Start(100, CancellationToken.None, () => Interlocked.Increment(ref abortCount));

        scope.Dispose();
        await Task.Delay(300);

        Assert.Equal(0, Volatile.Read(ref abortCount));
    }
}
