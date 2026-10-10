using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// <see cref="ITcpServer.FrameReceived"/> 事件的参数：未被会话内请求或接收等待者认领的入站帧，附带其所属会话。由服务端内部创建。
/// </summary>
public sealed class TcpSessionFrameEventArgs : EventArgs
{
    internal TcpSessionFrameEventArgs(ITcpSession session, FrameReceivedEventArgs frame)
    {
        Session = session;
        Data = frame.Data;
        ReceivedAt = frame.ReceivedAt;
    }

    /// <summary>帧所属的会话。</summary>
    public ITcpSession Session { get; }

    /// <summary>帧内容的独立副本。</summary>
    public byte[] Data { get; }

    /// <summary>接收时间。</summary>
    public DateTimeOffset ReceivedAt { get; }
}
