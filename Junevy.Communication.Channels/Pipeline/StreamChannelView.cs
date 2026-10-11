using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 字节流连接在握手与心跳期间的字节通道视图（P25 Task 11，补审第 2 节）：绑定到这一条连接，不派发 <c>FrameReceived</c>
/// （握手期间未认领的帧进入积压，握手后才派发，D8）。字节流客户端驱动与 TCP 服务端会话共用。
/// </summary>
internal sealed class StreamChannelView : IByteChannel
{
    private readonly StreamChannel channel;

    /// <summary>创建视图。</summary>
    /// <param name="channel">连接的字节通道。</param>
    public StreamChannelView(StreamChannel channel)
    {
        this.channel = channel;
    }

    /// <inheritdoc />
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => channel.SendAsync(payload, cancellationToken);

    /// <inheritdoc />
    public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                 CancellationToken cancellationToken = default)
        => channel.RequestAsync(payload, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
        => channel.ReceiveAsync(options, cancellationToken);

    /// <summary>握手与心跳期间不派发事件：订阅被忽略。</summary>
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived
    {
        add
        {
        }

        remove
        {
        }
    }
}
