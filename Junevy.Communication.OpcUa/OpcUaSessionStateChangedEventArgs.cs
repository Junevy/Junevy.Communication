namespace Junevy.Communication.OpcUa;

/// <summary>
/// 会话状态变化的通知（P62-D11）。只在状态真的改变时发出，顺序与实际变化一致。
/// </summary>
public sealed class OpcUaSessionStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// 构造一次状态变化。
    /// </summary>
    /// <param name="previous">变化前的状态。</param>
    /// <param name="current">变化后的状态。</param>
    /// <param name="reason">原因（SDK 给出的原因文本）；没有时为 null。</param>
    public OpcUaSessionStateChangedEventArgs(OpcUaSessionState previous, OpcUaSessionState current, string? reason)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    /// <summary>变化前的状态。</summary>
    public OpcUaSessionState Previous { get; }

    /// <summary>变化后的状态。</summary>
    public OpcUaSessionState Current { get; }

    /// <summary>原因（SDK 给出的原因文本）；没有时为 null。</summary>
    public string? Reason { get; }
}
