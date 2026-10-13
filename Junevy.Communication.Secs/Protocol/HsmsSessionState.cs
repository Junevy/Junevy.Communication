using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Secs.Hsms;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// HSMS 会话状态的唯一写入口（Q35、P51-D10）。状态只在这里变，事件按发生顺序发出，
/// 订阅者抛异常不影响状态机（走 Core 的 <see cref="EventSubscribers"/>）。
/// </summary>
internal sealed class HsmsSessionState
{
    private readonly ILogger logger;
    private readonly object gate = new object();
    private HsmsState state = HsmsState.NotConnected;

    /// <summary>创建状态持有者，初始为 <see cref="HsmsState.NotConnected"/>。</summary>
    /// <param name="logger">用于记录状态变化的日志记录器。</param>
    internal HsmsSessionState(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>变化通知；按发生顺序发出。</summary>
    internal event EventHandler<HsmsStateChangedEventArgs>? StateChanged;

    /// <summary>当前状态；读的是快照，调用方不持有锁。</summary>
    internal HsmsState State
    {
        get
        {
            lock (gate)
                return state;
        }
    }

    /// <summary>切到新状态。相同状态是空操作，因此"重复收到 Select.req"不会多发一次事件。</summary>
    /// <param name="next">目标状态。</param>
    /// <param name="reason">原因说明，写进日志与事件参数。</param>
    internal void MoveTo(HsmsState next, string reason)
    {
        EventHandler<HsmsStateChangedEventArgs>? handlers;
        HsmsState previous;
        lock (gate)
        {
            if (state == next)
                return;

            previous = state;
            state = next;
            logger.LogInformation("HSMS state changed from {Previous} to {Current}: {Reason}.", previous, next, reason);
            handlers = StateChanged;
        }

        // 在锁外派发：订阅者里可能再去读 State 或发起 StopAsync。
        EventSubscribers.InvokeEach(handlers, this, new HsmsStateChangedEventArgs(previous, next, reason), logger, nameof(StateChanged));
    }

    /// <summary>无条件切到 <see cref="HsmsState.NotConnected"/>（连接断了）。</summary>
    /// <param name="reason">原因说明。</param>
    internal void Reset(string reason) => MoveTo(HsmsState.NotConnected, reason);
}