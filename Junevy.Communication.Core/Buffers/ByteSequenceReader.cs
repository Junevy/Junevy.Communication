using System.Buffers;

namespace Junevy.Communication.Core.Buffers;

/// <summary>
/// 在 <see cref="ReadOnlySequence{T}"/> 上前进式读取字节。成员名与 <c>System.Buffers.SequenceReader&lt;T&gt;</c> 一致
/// （net472 没有该类型，P25-D6）；以后去掉 net472 时可直接替换。跨段读取只在段边界取下一段，不复制数据。
/// </summary>
public ref struct ByteSequenceReader
{
    private SequencePosition nextSegment;
    private ReadOnlyMemory<byte> segment;
    private int index;
    private long consumed;

    /// <summary>创建读取器，位置在序列开头。</summary>
    /// <param name="sequence">要读取的序列。</param>
    public ByteSequenceReader(ReadOnlySequence<byte> sequence)
    {
        Sequence = sequence;
        nextSegment = sequence.Start;
        segment = ReadOnlyMemory<byte>.Empty;
        index = 0;
        consumed = 0;
    }

    /// <summary>被读取的序列。</summary>
    public ReadOnlySequence<byte> Sequence { get; }

    /// <summary>当前位置，即下一个未读字节的位置；等于 <c>Sequence.GetPosition(Consumed)</c>。</summary>
    public SequencePosition Position => Sequence.GetPosition(consumed);

    /// <summary>已读取（或跳过）的字节数。</summary>
    public long Consumed => consumed;

    /// <summary>尚未读取的字节数。</summary>
    public long Remaining => Sequence.Length - consumed;

    /// <summary>是否已到序列末尾。</summary>
    public bool End => Remaining == 0;

    /// <summary>读取下一个字节并前进。</summary>
    /// <param name="value">读到的字节；没有更多字节时为 0。</param>
    /// <returns>读到字节返回 true；序列已结束返回 false。</returns>
    public bool TryRead(out byte value)
    {
        if (!TryPeek(out value))
            return false;

        index++;
        consumed++;
        return true;
    }

    /// <summary>查看下一个字节，不前进。</summary>
    /// <param name="value">下一个字节；没有更多字节时为 0。</param>
    /// <returns>有下一个字节返回 true。</returns>
    public bool TryPeek(out byte value)
    {
        if (ReadableInSegment() == 0)
        {
            value = 0;
            return false;
        }

        value = segment.Span[index];
        return true;
    }

    /// <summary>前进指定的字节数。</summary>
    /// <param name="count">要前进的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 为负或超过 <see cref="Remaining"/>。</exception>
    public void Advance(long count)
    {
        if (count < 0 || count > Remaining)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Cannot advance beyond the remaining bytes of the sequence.");

        while (count > 0)
        {
            int step = (int)Math.Min(count, ReadableInSegment());
            index += step;
            consumed += step;
            count -= step;
        }
    }

    // 返回当前段中可读的字节数。当前段读完时取下一段（跳过空段）；序列结束时返回 0。
    private int ReadableInSegment()
    {
        while (index >= segment.Length)
        {
            if (!Sequence.TryGet(ref nextSegment, out segment, advance: true))
                return 0;

            index = 0;
        }

        return segment.Length - index;
    }
}
