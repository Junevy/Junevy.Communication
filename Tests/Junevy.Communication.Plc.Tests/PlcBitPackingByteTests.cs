using Junevy.Communication.Plc.Pipeline;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 字节中的位（P4-D6）：字节软元件的位序——点 p 在第 <c>p / 8</c> 个字节的第 <c>p % 8</c> 位，位 0 是字节的最低位。
/// </summary>
public sealed class PlcBitPackingByteTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(0, 8, 1)]
    [InlineData(7, 1, 1)]
    [InlineData(7, 2, 2)]
    [InlineData(5, 3, 1)]
    [InlineData(5, 4, 2)]
    [InlineData(0, 9, 2)]
    public void ByteCount_CoversTheBitRange(int bitStart, int count, int expected)
    {
        Assert.Equal(expected, PlcBitPacking.ByteCount(bitStart, count));
    }

    [Fact]
    public void ExtractFromBytes_Byte05_HasBitsZeroAndTwoSet()
    {
        // 黄金样例（计划第 7 节第 3 条）：字节 0x05 → 位 0 为 true、位 1 为 false、位 2 为 true。
        byte[] bytes = { 0x05 };

        bool[] bits = PlcBitPacking.ExtractFromBytes(bytes, 0, 3);

        Assert.Equal(new[] { true, false, true }, bits);
    }

    [Fact]
    public void ExtractFromBytes_ReadsEveryBitOfTheByteFromItsLowBit()
    {
        byte[] bytes = { 0x05 };

        bool[] bits = PlcBitPacking.ExtractFromBytes(bytes, 0, 8);

        Assert.Equal(new[] { true, false, true, false, false, false, false, false }, bits);
    }

    [Fact]
    public void ExtractFromBytes_StartsInTheMiddleOfTheFirstByteAndCrossesIntoTheNext()
    {
        byte[] bytes = { 0x05, 0x03 };

        // 点 6、7 属于第一个字节（都是 0），点 8、9 是第二个字节的位 0、位 1（0x03 → 都为 1）。
        bool[] bits = PlcBitPacking.ExtractFromBytes(bytes, 6, 4);

        Assert.Equal(new[] { false, false, true, true }, bits);
    }

    [Fact]
    public void WordCount_StillUsesSixteenBitsPerWord()
    {
        Assert.Equal(1, PlcBitPacking.WordCount(0, 8));
        Assert.Equal(1, PlcBitPacking.WordCount(7, 2));
        Assert.Equal(2, PlcBitPacking.WordCount(8, 9));
    }
}
