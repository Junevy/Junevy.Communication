using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// StartEnd 分帧：丢弃起始符之前的字节（重新同步），从起始符之后查找结束符。
/// 未找到结束符且从起始符算起超过 MaxFrameLength 时抛出 <see cref="FrameDecodeException"/>。
/// </summary>
internal sealed class StartEndFrameDecoder : IFrameDecoder
{
    private readonly BytePattern startMarker;
    private readonly BytePattern endMarker;
    private readonly bool keepMarkers;
    private readonly int maxFrameLength;

    public StartEndFrameDecoder(BytePattern startMarker, BytePattern endMarker, bool keepMarkers, int maxFrameLength)
    {
        this.startMarker = startMarker;
        this.endMarker = endMarker;
        this.keepMarkers = keepMarkers;
        this.maxFrameLength = maxFrameLength;
    }

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;

        // 重新同步：丢弃起始符之前的字节。尾部可能是尚未收全的起始符，因此保留最后 (长度 - 1) 个字节。
        long start = startMarker.IndexOf(buffer, 0);
        if (start < 0)
        {
            long keep = Math.Min(buffer.Length, startMarker.Length - 1);
            buffer = buffer.Slice(buffer.Length - keep);
            return false;
        }

        if (start > 0)
            buffer = buffer.Slice(start);

        long end = endMarker.IndexOf(buffer, startMarker.Length);
        if (end < 0)
        {
            if (buffer.Length > maxFrameLength)
                throw new FrameDecodeException($"Frame started by the start marker exceeds MaxFrameLength ({maxFrameLength} bytes) without an end marker.");

            return false;
        }

        long frameLength = end + endMarker.Length;
        if (frameLength > maxFrameLength)
            throw new FrameDecodeException($"Frame length {frameLength} exceeds MaxFrameLength ({maxFrameLength} bytes).");

        frame = keepMarkers
            ? buffer.Slice(0, frameLength)
            : buffer.Slice(startMarker.Length, end - startMarker.Length);
        buffer = buffer.Slice(frameLength);
        return true;
    }
}
