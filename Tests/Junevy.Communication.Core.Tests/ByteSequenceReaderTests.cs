using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="ByteSequenceReader"/> 的前进式读取测试：多段（含空段）、预览、跨段前进、位置与越界。
/// </summary>
public sealed class ByteSequenceReaderTests
{
    private static readonly byte[] Payload = { 1, 2, 3, 4, 5, 6 };

    [Fact]
    public void TryRead_ReturnsEveryByteAcrossSegmentsIncludingEmptyOnes()
    {
        ReadOnlySequence<byte> sequence = SequenceFactory.FromSegments(
            new byte[] { 1, 2 }, Array.Empty<byte>(), new byte[] { 3 }, Array.Empty<byte>(), new byte[] { 4, 5, 6 });
        var reader = new ByteSequenceReader(sequence);
        var read = new List<byte>();

        while (reader.TryRead(out byte value))
            read.Add(value);

        Assert.Equal(Payload, read);
        Assert.True(reader.End);
    }

    [Fact]
    public void TryPeek_ReturnsTheNextByteWithoutAdvancing()
    {
        var reader = new ByteSequenceReader(SequenceFactory.FromSegments(new byte[] { 9 }, new byte[] { 8 }));

        Assert.True(reader.TryPeek(out byte first));
        Assert.True(reader.TryPeek(out byte again));
        Assert.Equal(9, first);
        Assert.Equal(9, again);
        Assert.Equal(0, reader.Consumed);

        Assert.True(reader.TryRead(out byte read));
        Assert.Equal(9, read);
    }

    [Fact]
    public void Advance_CrossesSegmentBoundaries()
    {
        var reader = new ByteSequenceReader(SequenceFactory.FromSegments(new byte[] { 1, 2 }, new byte[] { 3 }, new byte[] { 4, 5, 6 }));

        reader.Advance(4);

        Assert.Equal(4, reader.Consumed);
        Assert.Equal(2, reader.Remaining);
        Assert.True(reader.TryRead(out byte value));
        Assert.Equal(5, value);
    }

    [Fact]
    public void ConsumedPlusRemaining_EqualsLength_AndPositionPointsAtTheNextUnreadByte()
    {
        ReadOnlySequence<byte> sequence = SequenceFactory.FromSegments(new byte[] { 1, 2 }, Array.Empty<byte>(), new byte[] { 3, 4, 5, 6 });
        var reader = new ByteSequenceReader(sequence);

        for (int step = 0; step <= Payload.Length; step++)
        {
            Assert.Equal(sequence.Length, reader.Consumed + reader.Remaining);
            Assert.Equal(sequence.GetPosition(reader.Consumed), reader.Position);
            Assert.Equal(Payload.Skip(step), sequence.Slice(reader.Position).ToArray());

            if (step < Payload.Length)
                reader.Advance(1);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void Advance_OutsideTheRemainingBytes_Throws(long count)
    {
        ReadOnlySequence<byte> sequence = SequenceFactory.FromSegments(Payload);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteSequenceReader(sequence).Advance(count));
    }

    [Fact]
    public void EmptySequence_IsAtEnd()
    {
        var reader = new ByteSequenceReader(default);

        Assert.True(reader.End);
        Assert.Equal(0, reader.Remaining);
        Assert.False(reader.TryRead(out _));
        Assert.False(reader.TryPeek(out _));
    }
}
