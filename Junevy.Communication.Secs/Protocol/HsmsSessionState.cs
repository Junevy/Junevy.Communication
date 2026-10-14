using Junevy.Communication.Channels;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Secs.Hsms;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// HSMS 会话状态的唯一写入口（Q35、P51-D10）。状态只在这里变，事件按发生顺序发出，
/// 订阅者抛异常不影响状态机（走 Core 的 <see cref="EventSubscribers"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 状态有两个来源：握手（在通道的打开流程里同步执行）与链路事件（通道的 <c>StateChanged</c>、服务端的
/// <c>SessionConnected</c> / <c>SessionClosed</c>）。后者由通道与服务端各自的派发任务<strong>排队送出</strong>，
/// 可能晚于实际变化，甚至晚于下一条链路的握手。因此：
/// </para>
/// <list type="bullet">
/// <item>握手只把状态推到 <see cref="HsmsState.NotSelected"/>（开始）或复位（失败），成功时不直接报 <see cref="HsmsState.Selected"/>；</item>
/// <item><see cref="HsmsState.Selected"/> 只在链路被接管时报告（<see cref="LinkAttached"/>），并以链路此刻是否还在核对；</item>
/// <item>链路断开（<see cref="LinkLost"/>）只在没有新的握手进行、链路此刻也确实不在时复位。</item>
/// </list>
/// <para>
/// 这样 <see cref="HsmsState.Selected"/> 与"断开"都来自同一条有序的事件流，晚到的事件不会把新链路的状态覆盖掉。
/// </para>
/// </remarks>
internal sealed class HsmsSessionState
{
    private readonly ILogger logger;
    private readonly object gate = new object();
    private HsmsState state = HsmsState.NotConnected;
    private bool handshaking;

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

    /// <summary>新链路开始握手：进入 <see cref="HsmsState.NotSelected"/>，并标记握手进行中。</summary>
    /// <param name="reason">原因说明。</param>
    internal void BeginHandshake(string reason)
    {
        lock (gate)
            handshaking = true;

        MoveTo(HsmsState.NotSelected, reason);
    }

    /// <summary>握手成功：只结束握手标记；<see cref="HsmsState.Selected"/> 等链路被接管时再报告（见类型注释）。</summary>
    internal void EndHandshake()
    {
        lock (gate)
            handshaking = false;
    }

    /// <summary>
    /// 链路已被接管（主动端收到 <c>Connected</c>，被动端收到 <c>SessionConnected</c>）：链路此刻还在就进入
    /// <see cref="HsmsState.Selected"/>；已经不在了（晚到的事件）就什么都不做，随后的断开事件会复位。
    /// </summary>
    /// <param name="alive">链路此刻是否还在。</param>
    /// <param name="reason">原因说明。</param>
    internal void LinkAttached(bool alive, string reason)
    {
        if (alive)
            MoveTo(HsmsState.Selected, reason);
    }

    /// <summary>
    /// 链路断开：新链路的握手正在进行，或链路此刻又连上了，说明这是旧链路晚到的事件，不复位；否则复位。
    /// </summary>
    /// <param name="stillConnected">链路此刻是否仍然（或又）连着。</param>
    /// <param name="reason">原因说明。</param>
    internal void LinkLost(bool stillConnected, string reason)
    {
        lock (gate)
        {
            if (handshaking || stillConnected)
                return;
        }

        Reset(reason);
    }

    /// <summary>主动端：把客户端通道的一条状态事件（可能晚到）翻译成 <see cref="LinkAttached"/> 或 <see cref="LinkLost"/>。</summary>
    /// <param name="args">事件参数。</param>
    /// <param name="live">通道此刻的状态。</param>
    internal void ApplyClientEvent(ConnectionStateChangedEventArgs args, ConnectionState live)
    {
        if (args.CurrentState == ConnectionState.Connected)
            LinkAttached(live == ConnectionState.Connected, "Select.rsp 0 (ok) was received and the channel is connected.");
        else if (args.PreviousState == ConnectionState.Connected)
            LinkLost(live == ConnectionState.Connected, $"The channel changed to {args.CurrentState} ({args.Reason}).");
    }

    /// <summary>无条件切到 <see cref="HsmsState.NotConnected"/>，并结束握手标记（握手失败、停止、链路断开）。</summary>
    /// <param name="reason">原因说明。</param>
    internal void Reset(string reason)
    {
        lock (gate)
            handshaking = false;

        MoveTo(HsmsState.NotConnected, reason);
    }
}