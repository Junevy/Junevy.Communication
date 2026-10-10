using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// LengthField 分帧。帧总长 = 偏移 + 字段宽度 + 长度值 + 修正值；交出 [InitialBytesToStrip, 总长) 部分。
/// 非法的长度字段（ASCII 含非字符、总长小于头部、超过 MaxFrameLength）抛出 <see cref="FrameDecodeException"/>。
/// </summary>
internal sealed class LengthFieldFrameDecoder : IFrameDecoder
{
    private readonly int offset;
    private readonly int size;
    private readonly LengthFieldEncoding encoding;
    private readonly int adjustment;
    private readonly int stripBytes;
    private readonly int maxFrameLength;
    private readonly int headerEnd;

    public LengthFieldFrameDecoder(int offset, int size, LengthFieldEncoding encoding, int adjustment, int stripBytes, int maxFrameLength)
    {
        this.offset = offset;
        this.size = size;
        this.encoding = encoding;
        this.adjustment = adjustment;
        this.stripBytes = stripBytes;
        this.maxFrameLength = maxFrameLength;
        headerEnd = offset + size;
    }

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        if (buffer.Length < headerEnd)
            return false;

        long length = ReadLengthField(buffer);
        long total = headerEnd + length + adjustment;
        if (total < headerEnd)
            throw new FrameDecodeException($"Length field value {length} with adjustment {adjustment} gives a frame shorter than its header.");

        if (total > maxFrameLength)
            throw new FrameDecodeException($"Frame length {total} exceeds MaxFrameLength ({maxFrameLength} bytes).");

        if (buffer.Length < total)
            return false;

        frame = buffer.Slice(stripBytes, total - stripBytes);
        buffer = buffer.Slice(total);
        return true;
    }

    private long ReadLengthField(ReadOnlySequence<byte> buffer)
    {
        Span<byte> field = stackalloc byte[size];
        CopyTo(buffer.Slice(offset, size), field);

        long value = 0;
        switch (encoding)
        {
            case LengthFieldEncoding.BinaryBigEndian:
                for (int i = 0; i < field.Length; i++)
                    value = (value << 8) | field[i];
                return value;

            case LengthFieldEncoding.BinaryLittleEndian:
                for (int i = field.Length - 1; i >= 0; i--)
                    value = (value << 8) | field[i];
                return value;

            case LengthFieldEncoding.AsciiHex:
                for (int i = 0; i < field.Length; i++)
                {
                    int digit = HexDigitValue(field[i]);
                    if (digit < 0)
                        throw new FrameDecodeException($"Invalid hexadecimal character in the ASCII length field at offset {offset + i}.");

                    value = value * 16 + digit;
                }

                return value;

            case LengthFieldEncoding.AsciiDecimal:
                for (int i = 0; i < field.Length; i++)
                {
                    int digit = field[i] - '0';
                    if (digit < 0 || digit > 9)
                        throw new FrameDecodeException($"Invalid decimal character in the ASCII length field at offset {offset + i}.");

                    value = value * 10 + digit;
                }

                return value;

            default:
                throw new InvalidOperationException($"Unsupported length field encoding '{encoding}'.");
        }
    }

    private static int HexDigitValue(byte value)
    {
        if (value >= '0' && value <= '9')
            return value - '0';
        if (value >= 'A' && value <= 'F')
            return value - 'A' + 10;
        if (value >= 'a' && value <= 'f')
            return value - 'a' + 10;
        return -1;
    }

    /// <summary>把序列开头的 <paramref name="destination"/>.Length 个字节复制到目标（调用方保证序列足够长）。</summary>
    private static void CopyTo(ReadOnlySequence<byte> sequence, Span<byte> destination)
    {
        int written = 0;
        SequencePosition position = sequence.Start;
        while (written < destination.Length && sequence.TryGet(ref position, out ReadOnlyMemory<byte> memory))
        {
            ReadOnlySpan<byte> span = memory.Span;
            int count = Math.Min(span.Length, destination.Length - written);
            span.Slice(0, count).CopyTo(destination.Slice(written));
            written += count;
        }
    }
}
