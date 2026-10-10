namespace Junevy.Communication.Tcp;

/// <summary>
/// <see cref="ITcpServer.StateChanged"/> 事件的参数。由服务端内部创建。
/// </summary>
public sealed class ServerStateChangedEventArgs : EventArgs
{
    internal ServerStateChangedEventArgs(ServerState previousState, ServerState currentState, Exception? exception)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Exception = exception;
    }

    /// <summary>变化前的状态。</summary>
    public ServerState PreviousState { get; }

    /// <summary>变化后的状态。</summary>
    public ServerState CurrentState { get; }

    /// <summary>进入 Faulted 时的异常，仅供诊断；其余变化为 null。</summary>
    public Exception? Exception { get; }
}
