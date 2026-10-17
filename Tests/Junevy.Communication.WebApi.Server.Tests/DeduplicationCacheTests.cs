using Junevy.Communication.WebApi.Server.Dedup;
using Junevy.Communication.WebApi.Server.Pipeline;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>去重缓存的规则（P32-D11）：纯单元测试，时间由 <see cref="ManualTimeProvider"/> 推进。</summary>
public sealed class DeduplicationCacheTests
{
    private static readonly byte[] Body = new byte[] { 1, 2, 3 };

    [Fact]
    public void FirstRequest_Proceeds_AndIsCountedAsInFlight()
    {
        var cache = new DeduplicationCache(60000, 10, new ManualTimeProvider());

        DedupDecision decision = cache.Begin("k", "POST", "/orders", Body);

        Assert.Equal(DedupState.Proceed, decision.State);
        Assert.NotNull(decision.Ticket);
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(1, cache.InFlightCount);
    }

    [Fact]
    public void SameKey_WhileInFlight_IsInProgress()
    {
        var cache = new DeduplicationCache(60000, 10, new ManualTimeProvider());
        cache.Begin("k", "POST", "/orders", Body);

        DedupDecision decision = cache.Begin("k", "POST", "/orders", Body);

        Assert.Equal(DedupState.InProgress, decision.State);
    }

    [Fact]
    public void SameKey_WithADifferentBody_IsABodyMismatch_EvenWhileInFlight()
    {
        var cache = new DeduplicationCache(60000, 10, new ManualTimeProvider());
        cache.Begin("k", "POST", "/orders", Body);

        DedupDecision decision = cache.Begin("k", "POST", "/orders", new byte[] { 9 });

        Assert.Equal(DedupState.BodyMismatch, decision.State);
    }

    [Fact]
    public void Method_AndPath_ArePartOfTheKey()
    {
        var cache = new DeduplicationCache(60000, 10, new ManualTimeProvider());
        cache.Begin("k", "POST", "/orders", Body);

        Assert.Equal(DedupState.Proceed, cache.Begin("k", "PUT", "/orders", Body).State);
        Assert.Equal(DedupState.Proceed, cache.Begin("k", "POST", "/other", Body).State);
    }

    [Fact]
    public void Completed_WithinTheWindow_ReplaysTheCachedResult()
    {
        var cache = new DeduplicationCache(60000, 10, new ManualTimeProvider());
        DedupDecision first = cache.Begin("k", "POST", "/orders", Body);
        var result = new ServerResult(201, null, WebApiServerOutcome.Succeeded);

        first.Ticket!.Complete(result);
        DedupDecision second = cache.Begin("k", "POST", "/orders", Body);

        Assert.Equal(DedupState.Replay, second.State);
        Assert.Same(result, second.Replayed);
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public void Abandoned_IsRemoved_SoTheKeyRunsAgain()
    {
        var cache = new DeduplicationCache(60000, 10, new ManualTimeProvider());
        cache.Begin("k", "POST", "/orders", Body).Ticket!.Abandon();

        DedupDecision decision = cache.Begin("k", "POST", "/orders", Body);

        Assert.Equal(DedupState.Proceed, decision.State);
    }

    [Fact]
    public void Completed_AfterTheWindow_IsForgotten()
    {
        var time = new ManualTimeProvider();
        var cache = new DeduplicationCache(1000, 10, time);
        cache.Begin("k", "POST", "/orders", Body).Ticket!.Complete(new ServerResult(200, null, WebApiServerOutcome.Succeeded));

        time.Advance(TimeSpan.FromMilliseconds(1000));

        Assert.Equal(DedupState.Proceed, cache.Begin("k", "POST", "/orders", Body).State);
    }

    [Fact]
    public void WhenFull_TheOldestCompletedEntryIsEvicted()
    {
        var cache = new DeduplicationCache(60000, 2, new ManualTimeProvider());
        cache.Begin("a", "POST", "/x", Body).Ticket!.Complete(new ServerResult(200, null, WebApiServerOutcome.Succeeded));
        cache.Begin("b", "POST", "/x", Body).Ticket!.Complete(new ServerResult(200, null, WebApiServerOutcome.Succeeded));

        DedupDecision third = cache.Begin("c", "POST", "/x", Body);

        Assert.Equal(DedupState.Proceed, third.State);
        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(DedupState.Proceed, cache.Begin("a", "POST", "/x", Body).State);
    }

    [Fact]
    public void WhenFullOfInFlightEntries_NothingIsEvicted_AndTheRequestIsRefused()
    {
        var cache = new DeduplicationCache(60000, 1, new ManualTimeProvider());
        cache.Begin("a", "POST", "/x", Body);

        DedupDecision decision = cache.Begin("b", "POST", "/x", Body);

        Assert.Equal(DedupState.Full, decision.State);
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(DedupState.InProgress, cache.Begin("a", "POST", "/x", Body).State);
    }
}
