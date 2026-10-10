using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="ByteTransform"/> 与 <see cref="WordOrder"/> 的测试：黄金向量、手册锚点、往返、长度与字序校验。
/// </summary>
public sealed class ByteTransformTests
{
    [Theory]
    [InlineData(WordOrder.ABCD, new byte[] { 0x12, 0x34, 0x56, 0x78 })]
    [InlineData(WordOrder.BADC, new byte[] { 0x34, 0x12, 0x78, 0x56 })]
    [InlineData(WordOrder.CDAB, new byte[] { 0x56, 0x78, 0x12, 0x34 })]
    [InlineData(WordOrder.DCBA, new byte[] { 0x78, 0x56, 0x34, 0x12 })]
    public void UInt32_0x12345678_MatchesTheDocumentedLayout(WordOrder order, byte[] layout)
    {
        Assert.Equal(0x12345678u, ByteTransform.ToUInt32(layout, order));
        Assert.Equal(layout, WriteToArray(4, (destination) => ByteTransform.WriteUInt32(destination, 0x12345678u, order)));
    }

    [Theory]
    [InlineData(WordOrder.ABCD, new byte[] { 0x12, 0x34 })]
    [InlineData(WordOrder.BADC, new byte[] { 0x34, 0x12 })]
    [InlineData(WordOrder.CDAB, new byte[] { 0x12, 0x34 })]
    [InlineData(WordOrder.DCBA, new byte[] { 0x34, 0x12 })]
    public void UInt16_0x1234_IsBigEndianForAbcdAndCdab(WordOrder order, byte[] layout)
    {
        Assert.Equal((ushort)0x1234, ByteTransform.ToUInt16(layout, order));
        Assert.Equal(layout, WriteToArray(2, (destination) => ByteTransform.WriteUInt16(destination, 0x1234, order)));
    }

    [Theory]
    [InlineData(WordOrder.ABCD, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 })]
    [InlineData(WordOrder.BADC, new byte[] { 0x02, 0x01, 0x04, 0x03, 0x06, 0x05, 0x08, 0x07 })]
    [InlineData(WordOrder.CDAB, new byte[] { 0x07, 0x08, 0x05, 0x06, 0x03, 0x04, 0x01, 0x02 })]
    [InlineData(WordOrder.DCBA, new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 })]
    public void UInt64_0x0102030405060708_MatchesTheDocumentedLayout(WordOrder order, byte[] layout)
    {
        Assert.Equal(0x0102030405060708ul, ByteTransform.ToUInt64(layout, order));
        Assert.Equal(layout, WriteToArray(8, (destination) => ByteTransform.WriteUInt64(destination, 0x0102030405060708ul, order)));
    }

    [Theory]
    [InlineData(WordOrder.ABCD, new byte[] { 0x3F, 0x40, 0x00, 0x00 })]
    [InlineData(WordOrder.BADC, new byte[] { 0x40, 0x3F, 0x00, 0x00 })]
    [InlineData(WordOrder.CDAB, new byte[] { 0x00, 0x00, 0x3F, 0x40 })]
    [InlineData(WordOrder.DCBA, new byte[] { 0x00, 0x00, 0x40, 0x3F })]
    public void Single_0Point75_MatchesTheDocumentedLayout(WordOrder order, byte[] layout)
    {
        Assert.Equal(0.75f, ByteTransform.ToSingle(layout, order));
        Assert.Equal(layout, WriteToArray(4, (destination) => ByteTransform.WriteSingle(destination, 0.75f, order)));
    }

    [Fact]
    public void Single_0Point75_WrittenAsDcba_MatchesManualWordLayout()
    {
        // 手册 p.77：D0 = 0000H、D1 = 3F40H，按字小端传输。
        byte[] destination = new byte[4];

        ByteTransform.WriteSingle(destination, 0.75f, WordOrder.DCBA);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x40, 0x3F }, destination);
    }

    [Theory]
    [InlineData(WordOrder.ABCD, new byte[] { 0x3F, 0xE8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(WordOrder.BADC, new byte[] { 0xE8, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(WordOrder.CDAB, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F, 0xE8 })]
    [InlineData(WordOrder.DCBA, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE8, 0x3F })]
    public void Double_0Point75_MatchesTheDocumentedLayout(WordOrder order, byte[] layout)
    {
        Assert.Equal(0.75, ByteTransform.ToDouble(layout, order));
        Assert.Equal(layout, WriteToArray(8, (destination) => ByteTransform.WriteDouble(destination, 0.75, order)));
    }

    [Theory]
    [InlineData(WordOrder.ABCD)]
    [InlineData(WordOrder.BADC)]
    [InlineData(WordOrder.CDAB)]
    [InlineData(WordOrder.DCBA)]
    public void EveryType_RoundTripsThroughEveryWordOrder(WordOrder order)
    {
        byte[] buffer = new byte[8];

        ByteTransform.WriteInt16(buffer, short.MinValue, order);
        Assert.Equal(short.MinValue, ByteTransform.ToInt16(buffer, order));
        ByteTransform.WriteUInt16(buffer, ushort.MaxValue, order);
        Assert.Equal(ushort.MaxValue, ByteTransform.ToUInt16(buffer, order));
        ByteTransform.WriteInt32(buffer, int.MinValue, order);
        Assert.Equal(int.MinValue, ByteTransform.ToInt32(buffer, order));
        ByteTransform.WriteUInt32(buffer, 0xA1B2C3D4u, order);
        Assert.Equal(0xA1B2C3D4u, ByteTransform.ToUInt32(buffer, order));
        ByteTransform.WriteInt64(buffer, long.MinValue, order);
        Assert.Equal(long.MinValue, ByteTransform.ToInt64(buffer, order));
        ByteTransform.WriteUInt64(buffer, 0xDEADBEEFCAFEBABEul, order);
        Assert.Equal(0xDEADBEEFCAFEBABEul, ByteTransform.ToUInt64(buffer, order));
        ByteTransform.WriteSingle(buffer, -1.5f, order);
        Assert.Equal(-1.5f, ByteTransform.ToSingle(buffer, order));
        ByteTransform.WriteDouble(buffer, 1234.5678, order);
        Assert.Equal(1234.5678, ByteTransform.ToDouble(buffer, order));
    }

    [Fact]
    public void Read_TooFewBytes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ByteTransform.ToInt16(new byte[1], WordOrder.ABCD));
        Assert.Throws<ArgumentException>(() => ByteTransform.ToUInt32(new byte[3], WordOrder.DCBA));
        Assert.Throws<ArgumentException>(() => ByteTransform.ToInt64(new byte[7], WordOrder.CDAB));
        Assert.Throws<ArgumentException>(() => ByteTransform.ToDouble(new byte[7], WordOrder.BADC));
    }

    [Fact]
    public void Write_TooSmallDestination_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ByteTransform.WriteInt32(new byte[3], 1, WordOrder.ABCD));
        Assert.Throws<ArgumentException>(() => ByteTransform.WriteSingle(new byte[3], 1f, WordOrder.DCBA));
        Assert.Throws<ArgumentException>(() => ByteTransform.WriteUInt64(new byte[7], 1ul, WordOrder.BADC));
    }

    [Fact]
    public void Read_UsesOnlyTheLeadingBytes()
    {
        byte[] source = { 0x12, 0x34, 0xAA, 0xBB };

        Assert.Equal((ushort)0x1234, ByteTransform.ToUInt16(source, WordOrder.ABCD));
    }

    [Fact]
    public void Write_LeavesBytesAfterTheValueUnchanged()
    {
        byte[] destination = { 0x00, 0x00, 0xEE, 0xEE };

        ByteTransform.WriteUInt16(destination, 0x1234, WordOrder.BADC);

        Assert.Equal(new byte[] { 0x34, 0x12, 0xEE, 0xEE }, destination);
    }

    [Fact]
    public void UndefinedWordOrder_ThrowsArgumentOutOfRangeException()
    {
        var undefined = (WordOrder)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => ByteTransform.ToInt16(new byte[2], undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteTransform.WriteInt16(new byte[2], 1, undefined));
    }

    // 用固定长度的缓冲区执行一次写入，返回写入的字节。Span 不能作为泛型参数，因此使用专用委托。
    private static byte[] WriteToArray(int length, SpanWrite write)
    {
        byte[] buffer = new byte[length];
        write(buffer);
        return buffer;
    }

    private delegate void SpanWrite(Span<byte> destination);
}
