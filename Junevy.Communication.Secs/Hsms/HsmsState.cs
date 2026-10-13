namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// HSMS 会话的状态（SEMI E37、P51-D10）。不压成通道的 <c>ConnectionState</c>——TCP 连上只说明字节流通了，
/// 只有 Select 成功才算会话可用（P51-D2）。
/// </summary>
public enum HsmsState
{
    /// <summary>没有连接。</summary>
    NotConnected,

    /// <summary>TCP 已连上，HSMS 会话已建立，但还没 Select：只能应答 Linktest 与控制消息。</summary>
    NotSelected,

    /// <summary>已 Select：可以收发数据消息。</summary>
    Selected,
}

/// <summary>
/// <c>HsmsEquipment.StateChanged</c> 的事件参数：变化前后的状态与一句话原因。
/// </summary>
public sealed class HsmsStateChangedEventArgs : EventArgs
{
    /// <summary>建事件参数。</summary>
    /// <param name="previous">变化前的状态。</param>
    /// <param name="current">变化后的状态。</param>
    /// <param name="reason">原因说明，可为 null。</param>
    public HsmsStateChangedEventArgs(HsmsState previous, HsmsState current, string? reason)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    /// <summary>变化前的状态。</summary>
    public HsmsState Previous { get; }

    /// <summary>变化后的状态。</summary>
    public HsmsState Current { get; }

    /// <summary>原因说明，可为 null。</summary>
    public string? Reason { get; }
}