using Junevy.Communication.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.OpcUa;

/// <summary>
/// 会话状态的唯一写入口（Q35）：状态只在这里变化，<c>StateChanged</c> 也只从这里发出。
/// 因此状态快照可以无锁读取；订阅者抛异常只记 Error，不影响状态本身，也不影响其他订阅者。
/// </summary>
internal sealed class OpcUaSessionStateMachine
{
    private readonly object sender;
    private readonly ILogger logger;
    private int state = (int)OpcUaSessionState.Disconnected;
    private string? reason;

    /// <summary>
    /// 构造状态机。
    /// </summary>
    /// <param name="sender">事件的 sender（客户端本身）。</param>
    /// <param name="logger">记录状态变化与订阅者异常的日志器。</param>
    public OpcUaSessionStateMachine(object sender, ILogger logger)
    {
        this.sender = sender;
        this.logger = logger;
    }

    /// <summary>状态变化事件；只在状态真的改变时发出，顺序与实际变化一致。</summary>
    public event EventHandler<OpcUaSessionStateChangedEventArgs>? StateChanged;

    /// <summary>当前状态。</summary>
    public OpcUaSessionState State => (OpcUaSessionState)Volatile.Read(ref state);

    /// <summary>最近一次状态变化的原因；从未变化过时为 null。</summary>
    public string? Reason => Volatile.Read(ref reason);

    /// <summary>
    /// 改到新状态。**状态相同时不发出事件**——SDK 的连接状态事件每次重试都会触发（F17），必须在这里合并。
    /// </summary>
    /// <param name="next">目标状态。</param>
    /// <param name="why">原因；没有时为 null。</param>
    public void MoveTo(OpcUaSessionState next, string? why)
    {
        OpcUaSessionState previous = (OpcUaSessionState)Interlocked.Exchange(ref state, (int)next);
        if (previous == next)
        {
            return;
        }

        Volatile.Write(ref reason, why);
        logger.LogInformation("OPC UA session state changed from {PreviousState} to {CurrentState}.", previous, next);
        EventSubscribers.InvokeEach(
            StateChanged,
            sender,
            new OpcUaSessionStateChangedEventArgs(previous, next, why),
            logger,
            "OpcUaClient.StateChanged");
    }

    /// <summary>
    /// 只有当前状态是 <paramref name="expected"/> 时才改到 <paramref name="next"/>。
    /// 用于"我方主动断开"与"SDK 报告断线"竞争时避免把状态改回去。
    /// </summary>
    /// <param name="expected">期望的当前状态。</param>
    /// <param name="next">目标状态。</param>
    /// <param name="why">原因；没有时为 null。</param>
    /// <returns>确实改到了 <paramref name="next"/> 时为 true。</returns>
    public bool TryMoveTo(OpcUaSessionState expected, OpcUaSessionState next, string? why)
    {
        if (Interlocked.CompareExchange(ref state, (int)next, (int)expected) != (int)expected)
        {
            return false;
        }

        Volatile.Write(ref reason, why);
        logger.LogInformation("OPC UA session state changed from {PreviousState} to {CurrentState}.", expected, next);
        EventSubscribers.InvokeEach(
            StateChanged,
            sender,
            new OpcUaSessionStateChangedEventArgs(expected, next, why),
            logger,
            "OpcUaClient.StateChanged");
        return true;
    }
}