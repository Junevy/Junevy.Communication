using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="FixedIntervalBackoff"/> 与 <see cref="ExponentialBackoff"/> 的算法与参数校验测试。
/// </summary>
public sealed class BackoffTests
{
    [Fact]
    public void Fixed_ReturnsIntervalUntilMaxAttempts()
    {
        var policy = new FixedIntervalBackoff(250, maxAttempts: 3);

        Assert.Equal(250, policy.GetDelay(1));
        Assert.Equal(250, policy.GetDelay(2));
        Assert.Equal(250, policy.GetDelay(3));
        Assert.Null(policy.GetDelay(4));
    }

    [Fact]
    public void Exponential_GrowsAndCaps()
    {
        var policy = new ExponentialBackoff(500, 5000, multiplier: 2.0, jitter: 0.0, maxAttempts: 0, random: new Random(42));
        int[] expected = { 500, 1000, 2000, 4000, 5000, 5000 };

        for (int attempt = 1; attempt <= expected.Length; attempt++)
            Assert.Equal(expected[attempt - 1], policy.GetDelay(attempt));
    }

    [Fact]
    public void Exponential_JitterWithinRange()
    {
        // attempt = 3 时基础等待 = 250 × 2² = 1000，允许区间 [0.8 × 1000, 1.2 × 1000] = [800, 1200]。
        var policy = new ExponentialBackoff(250, 10000, multiplier: 2.0, jitter: 0.2, random: new Random(7));
        var distinct = new HashSet<int>();

        for (int i = 0; i < 1000; i++)
        {
            int delay = policy.GetDelay(3).GetValueOrDefault(-1);
            Assert.InRange(delay, 800, 1200);
            distinct.Add(delay);
        }

        Assert.True(distinct.Count > 1, "Jitter should produce varying delays.");
    }

    [Fact]
    public async Task Exponential_ConcurrentCalls_DoNotThrow()
    {
        var policy = new ExponentialBackoff(100, 5000, random: new Random(1));
        var workers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 10000; i++)
                policy.GetDelay(i % 20 + 1);
        })).ToArray();

        await Task.WhenAll(workers);
    }

    [Fact]
    public void FixedInterval_NegativeArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedIntervalBackoff(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedIntervalBackoff(100, maxAttempts: -1));
    }

    [Fact]
    public void Exponential_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(-1, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(2000, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(100, 1000, multiplier: 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(100, 1000, jitter: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(100, 1000, maxAttempts: -1));
    }

    [Fact]
    public void GetDelay_AttemptBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedIntervalBackoff(100).GetDelay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(100, 1000).GetDelay(0));
    }
}
