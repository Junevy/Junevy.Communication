using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// 不可变的字节模式（分隔符或标记）。预先计算 KMP 失败表，可在多个分帧器之间共享。
/// 查找逐段顺序进行，因此模式跨越 <see cref="ReadOnlySequence{T}"/> 的段边界时同样能匹配，且不复制缓冲。
/// </summary>
internal sealed class BytePattern
{
    private readonly int[] failure;

    /// <summary>
    /// 创建模式。
    /// </summary>
    /// <param name="bytes">模式字节；不能为空。</param>
    public BytePattern(byte[] bytes)
    {
        Bytes = bytes;
        failure = BuildFailureTable(bytes);
    }

    /// <summary>模式字节。</summary>
    public byte[] Bytes { get; }

    /// <summary>模式长度（字节）。</summary>
    public int Length => Bytes.Length;

    /// <summary>
    /// 查找第一次完整出现的位置，只考虑起点不早于 <paramref name="from"/> 的匹配。
    /// </summary>
    /// <param name="sequence">被搜索的序列。</param>
    /// <param name="from">起点下限（相对序列起点）。</param>
    /// <returns>匹配起点；未找到返回 -1。</returns>
    public long IndexOf(ReadOnlySequence<byte> sequence, long from)
    {
        int matched = 0;
        long segmentStart = 0;
        SequencePosition position = sequence.Start;
        while (sequence.TryGet(ref position, out ReadOnlyMemory<byte> memory))
        {
            ReadOnlySpan<byte> span = memory.Span;
            long segmentEnd = segmentStart + span.Length;
            if (segmentEnd > from)
            {
                int first = (int)Math.Max(0L, from - segmentStart);
                for (int i = first; i < span.Length; i++)
                {
                    byte value = span[i];
                    while (matched > 0 && value != Bytes[matched])
                        matched = failure[matched - 1];

                    if (value == Bytes[matched])
                        matched++;

                    if (matched == Bytes.Length)
                        return segmentStart + i - Bytes.Length + 1;
                }
            }

            segmentStart = segmentEnd;
        }

        return -1;
    }

    private static int[] BuildFailureTable(byte[] pattern)
    {
        var table = new int[pattern.Length];
        int k = 0;
        for (int i = 1; i < pattern.Length; i++)
        {
            while (k > 0 && pattern[i] != pattern[k])
                k = table[k - 1];

            if (pattern[i] == pattern[k])
                k++;

            table[i] = k;
        }

        return table;
    }
}
