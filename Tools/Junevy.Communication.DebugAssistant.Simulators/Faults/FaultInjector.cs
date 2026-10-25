namespace Junevy.Communication.DebugAssistant.Simulators.Faults;

/// <summary>
/// 持有当前的 <see cref="FaultPlan"/>（计划第 8.1 节）。界面在任何线程上替换设置，模拟器每处理一个请求取一次快照；
/// <see cref="FaultPlan.DisconnectOnNextRequest"/> 是一次性的，取出时清除。
/// </summary>
public sealed class FaultInjector
{
    /// <summary>应答延迟的上限（毫秒）：界面输入超过它时被拒绝，避免模拟器看起来像死机。</summary>
    public const int MaxDelayMilliseconds = 60_000;

    private readonly object _gate = new();
    private FaultPlan _plan = FaultPlan.None;

    /// <summary>当前的设置。替换时校验数值范围。</summary>
    public FaultPlan Plan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
        set
        {
            Validate(value);
            lock (_gate)
            {
                _plan = value;
            }
        }
    }

    /// <summary>取出处理下一个请求时使用的故障，并清除一次性的断开。</summary>
    public FaultPlan TakeForRequest()
    {
        lock (_gate)
        {
            FaultPlan current = _plan;
            if (current.DisconnectOnNextRequest)
            {
                _plan = current with { DisconnectOnNextRequest = false };
            }

            return current;
        }
    }

    private static void Validate(FaultPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(plan.DelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plan.DelayMilliseconds, MaxDelayMilliseconds);
        if (plan.ForcedErrorCode is { } code)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(code);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(code, 0xFFFF);
        }
    }
}
