using System.Buffers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 测试辅助：把字节数组切成多段 <see cref="ReadOnlySequence{T}"/>，用于验证分帧器对多段缓冲的处理。
/// </summary>
internal static class SequenceFactory
{
    /// <summary>
    /// 按固定段大小切分；最后一段可能较短。
    /// </summary>
    /// <param name="data">源数据。</param>
    /// <param name="segmentSize">每段的字节数，必须为正。</param>
    /// <returns>多段序列。</returns>
    public static ReadOnlySequence<byte> Segmented(byte[] data, int segmentSize)
    {
        if (segmentSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentSize), segmentSize, "Segment size must be positive.");

        var first = new Segment(new ReadOnlyMemory<byte>(data, 0, Math.Min(segmentSize, data.Length)));
        Segment last = first;
        for (int offset = segmentSize; offset < data.Length; offset += segmentSize)
        {
            int count = Math.Min(segmentSize, data.Length - offset);
            last = last.Append(new ReadOnlyMemory<byte>(data, offset, count));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    /// <summary>统计序列由多少段组成。</summary>
    /// <param name="sequence">待统计的序列。</param>
    /// <returns>段数。</returns>
    public static int CountSegments(ReadOnlySequence<byte> sequence)
    {
        int count = 0;
        SequencePosition position = sequence.Start;
        while (sequence.TryGet(ref position, out _))
            count++;

        return count;
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
