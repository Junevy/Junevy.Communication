using System.Net;

namespace Junevy.Communication.Channels;

/// <summary>
/// <see cref="IByteChannel.FrameReceived"/> 事件的参数。由通道内部创建。
/// </summary>
public sealed class FrameReceivedEventArgs : EventArgs
{
    internal FrameReceivedEventArgs(byte[] data, DateTimeOffset receivedAt, EndPoint? remoteEndPoint)
    {
        Data = data;
        ReceivedAt = receivedAt;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>帧内容的独立副本，调用方可以随意持有。</summary>
    public byte[] Data { get; }

    /// <summary>接收时间。</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>UDP 非定向模式下为来源地址；其余传输为 null。</summary>
    public EndPoint? RemoteEndPoint { get; }
}
