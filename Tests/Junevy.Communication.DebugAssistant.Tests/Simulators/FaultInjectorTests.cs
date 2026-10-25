using Junevy.Communication.DebugAssistant.Simulators.Faults;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>故障注入（计划 Task 11 第 2 条）：设置的读回与替换、数值范围、一次性的断开。</summary>
public sealed class FaultInjectorTests
{
    [Fact]
    public void Plan_IsReadBackAsItWasReplaced()
    {
        var injector = new FaultInjector();
        var plan = new FaultPlan { DelayMilliseconds = 250, NoReply = true, ForcedErrorCode = 0x0002, CorruptChecksum = true };

        injector.Plan = plan;

        Assert.Equal(plan, injector.Plan);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(60001, null)]
    [InlineData(0, -1)]
    [InlineData(0, 0x10000)]
    public void OutOfRangeValues_AreRejected_AndThePlanIsUnchanged(int delay, int? code)
    {
        var injector = new FaultInjector();

        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => injector.Plan = new FaultPlan { DelayMilliseconds = delay, ForcedErrorCode = code });
        Assert.Equal(FaultPlan.None, injector.Plan);
    }

    [Fact]
    public void TheDelayLimit_IsAccepted()
    {
        var injector = new FaultInjector();

        injector.Plan = new FaultPlan { DelayMilliseconds = FaultInjector.MaxDelayMilliseconds };

        Assert.Equal(FaultInjector.MaxDelayMilliseconds, injector.Plan.DelayMilliseconds);
    }

    [Fact]
    public void DisconnectOnNextRequest_IsConsumedByOneRequest_AndTheOtherFaultsStay()
    {
        var injector = new FaultInjector();
        injector.Plan = new FaultPlan { DelayMilliseconds = 10, DisconnectOnNextRequest = true };

        FaultPlan first = injector.TakeForRequest();
        FaultPlan second = injector.TakeForRequest();

        Assert.True(first.DisconnectOnNextRequest);
        Assert.False(second.DisconnectOnNextRequest);
        Assert.Equal(10, second.DelayMilliseconds);
        Assert.False(injector.Plan.DisconnectOnNextRequest);
    }

    [Fact]
    public void NoFault_IsTheDefault() => Assert.Equal(FaultPlan.None, new FaultPlan());
}
