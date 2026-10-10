using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// FixedLength 分帧：缓冲不少于 FrameLength 字节时交出固定长度的一帧。
/// </summary>
internal sealed class FixedLengthFrameDecoder : IFrameDecoder
{
    private readonly int frameLength;

    public FixedLengthFrameDecoder(int frameLength)
    {
        this.frameLength = frameLength;
    }

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        if (buffer.Length < frameLength)
        {
            frame = default;
            return false;
        }

        frame = buffer.Slice(0, frameLength);
        buffer = buffer.Slice(frameLength);
        return true;
    }
}
