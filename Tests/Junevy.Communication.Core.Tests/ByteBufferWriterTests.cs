using System.Buffers;
using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="ByteBufferWriter"/> 的写入、增长、清空、池化归还与释放后的行为测试。
/// </summary>
public sealed class ByteBufferWriterTests
{
    [Fact]
    public void Write_GrowsPastTheInitialCapacity()
    {
        using var writer = new ByteBufferWriter(4);
        byte[] expected = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();

        writer.Write(expected.AsSpan(0, 3));
        writer.Write(expected.AsSpan(3));

        Assert.Equal(expected, writer.ToArray());
        Assert.Equal(expected.Length, writer.WrittenCount);
    }

    [Fact]
    public void GetSpan_WithZeroSizeHint_ReturnsAtLeastOneByteWhenTheBufferIsFull()
    {
        using var writer = new ByteBufferWriter(2);
        writer.GetSpan(2);
        writer.Advance(2);

        Assert.True(writer.GetSpan().Length >= 1);
        Assert.True(writer.GetMemory().Length >= 1);
    }

    [Fact]
    public void Advance_BeyondTheReturnedSpace_Throws()
    {
        using var writer = new ByteBufferWriter(8);
        int available = writer.GetSpan(8).Length;

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(available + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
    }

    [Fact]
    public void Clear_KeepsTheBufferForReuse()
    {
        using var writer = new ByteBufferWriter(4);
        writer.Write(new byte[] { 1, 2, 3, 4, 5, 6 });

        writer.Clear();
        writer.Write(new byte[] { 7, 8 });

        Assert.Equal(new byte[] { 7, 8 }, writer.ToArray());
        Assert.Equal(2, writer.WrittenCount);
    }

    [Fact]
    public void ToArray_MatchesWrittenSpanAndWrittenMemory()
    {
        using var writer = new ByteBufferWriter();
        byte[] expected = { 0x10, 0x20, 0x30 };
        writer.Write(expected);

        Assert.Equal(expected, writer.ToArray());
        Assert.True(writer.WrittenSpan.SequenceEqual(expected));
        Assert.Equal(expected, writer.WrittenMemory.Span.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositiveInitialCapacity(int initialCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteBufferWriter(initialCapacity));
    }

    [Fact]
    public void Pooled_ReturnsEveryRentedArrayExactlyOnceAfterGrowthAndDispose()
    {
        var pool = new RecordingPool();
        var writer = new ByteBufferWriter(4, pool);

        writer.Write(new byte[20]);
        writer.Write(new byte[100]);
        writer.Dispose();

        Assert.True(pool.Rented.Count > 1, "The writer must grow at least once for this test.");
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Rented, array => Assert.Contains(array, pool.Returned));
    }

    [Fact]
    public void Dispose_CalledTwice_ReturnsThePooledArrayOnce()
    {
        var pool = new RecordingPool();
        var writer = new ByteBufferWriter(4, pool);

        writer.Dispose();
        writer.Dispose();

        Assert.Single(pool.Returned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AfterDispose_EveryMemberExceptDisposeThrows(bool pooled)
    {
        var writer = pooled ? new ByteBufferWriter(8, ArrayPool<byte>.Shared) : new ByteBufferWriter(8);
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.WrittenCount);
        Assert.Throws<ObjectDisposedException>(() => writer.WrittenMemory);
        Assert.Throws<ObjectDisposedException>(() => { _ = writer.WrittenSpan; });
        Assert.Throws<ObjectDisposedException>(() => writer.ToArray());
        Assert.Throws<ObjectDisposedException>(() => writer.Clear());
        Assert.Throws<ObjectDisposedException>(() => writer.Advance(0));
        Assert.Throws<ObjectDisposedException>(() => writer.GetMemory());
        Assert.Throws<ObjectDisposedException>(() => { _ = writer.GetSpan(); });
    }

    /// <summary>记录租借与归还的数组池，用于验证每个数组恰好归还一次。</summary>
    private sealed class RecordingPool : ArrayPool<byte>
    {
        public List<byte[]> Rented { get; } = new List<byte[]>();

        public List<byte[]> Returned { get; } = new List<byte[]>();

        public override byte[] Rent(int minimumLength)
        {
            byte[] array = new byte[minimumLength];
            Rented.Add(array);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false) => Returned.Add(array);
    }
}
