namespace Junevy.Communication.Tcp;

/// <summary>
/// 会话级事件的参数（<see cref="ITcpServer.SessionConnected"/>）。由服务端内部创建。
/// </summary>
public class TcpSessionEventArgs : EventArgs
{
    internal TcpSessionEventArgs(ITcpSession session)
    {
        Session = session;
    }

    /// <summary>相关会话。</summary>
    public ITcpSession Session { get; }
}
