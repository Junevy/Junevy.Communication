using System.Net;

namespace Junevy.Communication.Channels;

/// <summary>
/// <see cref="IByteChannel.FrameReceived"/> 事件的参数。通道内部创建；自定义通道也可以用公开构造函数创建后引发。
/// </summary>
public sealed class FrameReceivedEventArgs : EventArgs
{
    /// <summary>创建帧接收参数。<paramref name="data"/> 直接保存、不复制，调用方应传入之后不再修改的数组。</summary>
    /// <param name="data">帧内容。</param>
    /// <param name="receivedAt">接收时间。</param>
    /// <param name="remoteEndPoint">来源地址；只有 UDP 非定向模式有值，其余传 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> 为 null。</exception>
    public FrameReceivedEventArgs(byte[] data, DateTimeOffset receivedAt, EndPoint? remoteEndPoint)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
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
