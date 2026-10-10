using System.Text;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Plc.Pipeline;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 字中位的提取与写入（P2-D10）：位号与字的对应、字序对字值的影响，以及数据格式的默认值。
/// </summary>
public sealed class PlcBitPackingTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(0, 16, 1)]
    [InlineData(15, 1, 1)]
    [InlineData(15, 2, 2)]
    [InlineData(0, 17, 2)]
    public void WordCount_CoversTheBitRange(int bitStart, int count, int expected)
    {
        Assert.Equal(expected, PlcBitPacking.WordCount(bitStart, count));
    }

    [Fact]
    public void Extract_LittleEndianWord_ReadsFromBitZeroUpward()
    {
        // DCBA：字值 0x8005 的小端字节为 05 80。
        byte[] words = { 0x05, 0x80 };

        bool[] bits = PlcBitPacking.Extract(words, WordOrder.DCBA, 0, 16);

        Assert.True(bits[0]);
        Assert.False(bits[1]);
        Assert.True(bits[2]);
        Assert.False(bits[14]);
        Assert.True(bits[15]);
    }

    [Fact]
    public void Extract_BigEndianWord_UsesTheWordValueNotTheBytePosition()
    {
        // ABCD：字值 0x8005 的大端字节为 80 05。位号仍按字值计，与字节在存储区的顺序无关。
        byte[] words = { 0x80, 0x05 };

        bool[] bits = PlcBitPacking.Extract(words, WordOrder.ABCD, 0, 16);

        Assert.True(bits[0]);
        Assert.True(bits[2]);
        Assert.True(bits[15]);
    }

    [Fact]
    public void Extract_SpansTwoWords_FromTheMiddleOfTheFirst()
    {
        byte[] words = { 0x00, 0x80, 0x01, 0x00 };   // 字 0 = 0x8000，字 1 = 0x0001（DCBA）

        bool[] bits = PlcBitPacking.Extract(words, WordOrder.DCBA, 15, 2);

        Assert.Equal(new[] { true, true }, bits);
    }

    [Fact]
    public void Insert_SetsAndClearsOnlyTheTargetBits()
    {
        byte[] words = { 0xFF, 0xFF };

        PlcBitPacking.Insert(words, WordOrder.DCBA, 3, new[] { false });

        Assert.Equal(new byte[] { 0xF7, 0xFF }, words);
    }

    [Fact]
    public void Insert_BigEndianWord_WritesBackInTheSameOrder()
    {
        byte[] words = { 0x00, 0x00 };

        PlcBitPacking.Insert(words, WordOrder.ABCD, 15, new[] { true });

        Assert.Equal(new byte[] { 0x80, 0x00 }, words);
    }

    [Fact]
    public void Insert_ThenExtract_RoundTrips()
    {
        byte[] words = { 0x34, 0x12, 0x78, 0x56 };
        bool[] values = { true, false, true, true, false, false, true };

        PlcBitPacking.Insert(words, WordOrder.DCBA, 9, values);

        Assert.Equal(values, PlcBitPacking.Extract(words, WordOrder.DCBA, 9, values.Length));
    }

    [Fact]
    public void DataFormat_DefaultsToAscii_WhenNoEncodingIsGiven()
    {
        var format = new PlcDataFormat(WordOrder.DCBA);

        Assert.Equal(Encoding.ASCII, format.StringEncoding);
        Assert.Equal(WordOrder.DCBA, format.WordOrder);
    }

    [Fact]
    public void DataFormat_UndefinedWordOrder_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlcDataFormat((WordOrder)42));
    }
}
