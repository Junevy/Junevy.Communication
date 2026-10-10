using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// Raw 分帧：缓冲非空时交出全部字节；超过 MaxFrameLength 时按 MaxFrameLength 切块交出。
/// 没有消息边界，每次读到的数据块就是一帧（调试抓包用途）。
/// </summary>
internal sealed class RawFrameDecoder : IFrameDecoder
{
    private readonly int maxFrameLength;

    public RawFrameDecoder(int maxFrameLength)
    {
        this.maxFrameLength = maxFrameLength;
    }

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        if (buffer.Length == 0)
        {
            frame = default;
            return false;
        }

        long length = Math.Min(buffer.Length, maxFrameLength);
        frame = buffer.Slice(0, length);
        buffer = buffer.Slice(length);
        return true;
    }
}
