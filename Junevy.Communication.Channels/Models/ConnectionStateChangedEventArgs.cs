namespace Junevy.Communication.Channels;

/// <summary>
/// <see cref="IConnectable.StateChanged"/> 事件的参数。通道内部创建；自定义通道也可以用公开构造函数创建后引发。
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>创建状态变化参数，保存全部参数。</summary>
    /// <param name="previousState">变化前的状态。</param>
    /// <param name="currentState">变化后的状态。</param>
    /// <param name="reason">导致变化的原因；非断开类的变化传 <see cref="DisconnectReason.None"/>。</param>
    /// <param name="exception">相关异常，仅供诊断；没有时传 null。</param>
    /// <param name="reconnectAttempt">重连尝试序号；与重连无关的变化传 0。</param>
    public ConnectionStateChangedEventArgs(ConnectionState previousState, ConnectionState currentState,
                                           DisconnectReason reason, Exception? exception, int reconnectAttempt)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Reason = reason;
        Exception = exception;
        ReconnectAttempt = reconnectAttempt;
    }

    /// <summary>变化前的状态。</summary>
    public ConnectionState PreviousState { get; }

    /// <summary>变化后的状态。</summary>
    public ConnectionState CurrentState { get; }

    /// <summary>导致变化的原因；非断开类的变化为 <see cref="DisconnectReason.None"/>。</summary>
    public DisconnectReason Reason { get; }

    /// <summary>相关异常，仅供诊断；没有时为 null。</summary>
    public Exception? Exception { get; }

    /// <summary>重连尝试序号：与重连相关的变化为尝试序号，其余为 0。</summary>
    public int ReconnectAttempt { get; }
}
