using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// <see cref="ITcpServer.SessionClosed"/> 事件的参数。由服务端内部创建。
/// </summary>
public sealed class TcpSessionClosedEventArgs : TcpSessionEventArgs
{
    internal TcpSessionClosedEventArgs(ITcpSession session, DisconnectReason reason, Exception? exception)
        : base(session)
    {
        Reason = reason;
        Exception = exception;
    }

    /// <summary>会话关闭的原因（对端关闭为 <see cref="DisconnectReason.RemoteClosed"/>，服务端停止为 <see cref="DisconnectReason.UserRequested"/>）。</summary>
    public DisconnectReason Reason { get; }

    /// <summary>相关异常，仅供诊断；没有时为 null。</summary>
    public Exception? Exception { get; }
}
