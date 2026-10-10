using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// Delimiter 分帧：在缓冲中查找所有分隔符的最早出现位置（位置相同取最长的分隔符），交出其前的内容。
/// 连续分隔符之间的空内容不交付（D18）。找不到分隔符且缓冲超过 MaxFrameLength + 最长分隔符长度时抛出 <see cref="FrameDecodeException"/>。
/// </summary>
internal sealed class DelimiterFrameDecoder : IFrameDecoder
{
    private readonly BytePattern[] delimiters;
    private readonly bool keepDelimiter;
    private readonly int maxFrameLength;
    private readonly int maxDelimiterLength;

    public DelimiterFrameDecoder(BytePattern[] delimiters, bool keepDelimiter, int maxFrameLength)
    {
        this.delimiters = delimiters;
        this.keepDelimiter = keepDelimiter;
        this.maxFrameLength = maxFrameLength;

        foreach (BytePattern delimiter in delimiters)
            maxDelimiterLength = Math.Max(maxDelimiterLength, delimiter.Length);
    }

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        while (TryFindEarliest(buffer, out long start, out int delimiterLength))
        {
            if (start > maxFrameLength)
                throw new FrameDecodeException($"Frame content exceeds MaxFrameLength ({maxFrameLength} bytes) before its delimiter.");

            if (start == 0)
            {
                // D18：连续分隔符之间的空内容不交付，直接跳过该分隔符。
                buffer = buffer.Slice(delimiterLength);
                continue;
            }

            long frameLength = keepDelimiter ? start + delimiterLength : start;
            frame = buffer.Slice(0, frameLength);
            buffer = buffer.Slice(start + delimiterLength);
            return true;
        }

        if (buffer.Length > maxFrameLength + (long)maxDelimiterLength)
            throw new FrameDecodeException($"No delimiter found within {maxFrameLength} bytes; the frame is too long.");

        frame = default;
        return false;
    }

    private bool TryFindEarliest(ReadOnlySequence<byte> buffer, out long start, out int length)
    {
        start = -1;
        length = 0;
        foreach (BytePattern delimiter in delimiters)
        {
            long index = delimiter.IndexOf(buffer, 0);
            if (index < 0)
                continue;

            // 最早位置优先；位置相同时取最长的分隔符。
            if (start < 0 || index < start || (index == start && delimiter.Length > length))
            {
                start = index;
                length = delimiter.Length;
            }
        }

        return start >= 0;
    }
}
