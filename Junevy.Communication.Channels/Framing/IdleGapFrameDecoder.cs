using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// IdleGap 分帧：没有分隔符也没有长度字段，帧以静默时间判定。
/// <see cref="TryDecode"/> 从不交出数据，缓冲达到 MaxFrameLength 时抛出 <see cref="FrameDecodeException"/>；
/// <see cref="TryFlush"/> 在静默超时后交出全部缓冲。
/// </summary>
internal sealed class IdleGapFrameDecoder : IFlushableFrameDecoder
{
    private readonly int maxFrameLength;

    public IdleGapFrameDecoder(int gapTimeout, int maxFrameLength)
    {
        FlushTimeout = gapTimeout;
        this.maxFrameLength = maxFrameLength;
    }

    public int FlushTimeout { get; }

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        if (buffer.Length >= maxFrameLength)
            throw new FrameDecodeException($"Buffered data reached MaxFrameLength ({maxFrameLength} bytes) without an idle gap.");

        frame = default;
        return false;
    }

    public bool TryFlush(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        if (buffer.Length == 0)
        {
            frame = default;
            return false;
        }

        if (buffer.Length > maxFrameLength)
            throw new FrameDecodeException($"Buffered frame length {buffer.Length} exceeds MaxFrameLength ({maxFrameLength} bytes).");

        frame = buffer;
        buffer = buffer.Slice(buffer.Length);
        return true;
    }
}
