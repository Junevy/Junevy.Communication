namespace Junevy.Communication.Mqtt;

/// <summary>
/// 连接状态变化的参数（P61-D5）。按发生顺序通知，订阅者抛异常不影响状态本身，也不影响后续订阅者。
/// </summary>
public sealed class MqttConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>构造状态变化的参数。</summary>
    /// <param name="previous">变化前的状态。</param>
    /// <param name="current">变化后的状态。</param>
    /// <param name="reason">变化原因；没有特别原因时为 null。</param>
    public MqttConnectionStateChangedEventArgs(MqttConnectionState previous, MqttConnectionState current, string? reason)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    /// <summary>变化前的状态。</summary>
    public MqttConnectionState Previous { get; }

    /// <summary>变化后的状态。</summary>
    public MqttConnectionState Current { get; }

    /// <summary>变化原因；例如 Broker 拒绝连接时的原因码文本，或断开时的原因。没有特别原因时为 null。</summary>
    public string? Reason { get; }
}