using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Plc;

/// <summary>面板的值与文本的转换（计划 Task 7 第 3 条）：每种类型取边界值，超出范围与格式错误都给出 <see cref="FormatException"/>。</summary>
public sealed class PlcValueCodecTests
{
    [Theory]
    [InlineData("-32768", -32768)]
    [InlineData("32767", 32767)]
    [InlineData("0xFFFF", -1)]
    [InlineData("0x8000", -32768)]
    public void Int16_BoundaryValues_AndBitPatterns(string text, short expected)
    {
        Assert.Equal(new[] { expected }, (short[])PlcValueCodec.Parse(PlcValueType.Int16, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("32768")]
    [InlineData("-32769")]
    [InlineData("0x10000")]
    [InlineData("abc")]
    [InlineData("")]
    public void Int16_OutOfRangeOrMalformed_IsAFormatError(string text)
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Int16, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("65535", 65535)]
    [InlineData("0xFFFF", 65535)]
    public void UInt16_BoundaryValues(string text, ushort expected)
    {
        Assert.Equal(new[] { expected }, (ushort[])PlcValueCodec.Parse(PlcValueType.UInt16, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("65536")]
    [InlineData("-1")]
    public void UInt16_OutOfRange_IsAFormatError(string text)
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.UInt16, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("0xFFFFFFFF", -1)]
    public void Int32_BoundaryValues(string text, int expected)
    {
        Assert.Equal(new[] { expected }, (int[])PlcValueCodec.Parse(PlcValueType.Int32, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void Int32_OutOfRange_IsAFormatError(string text)
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Int32, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("0", 0u)]
    [InlineData("4294967295", uint.MaxValue)]
    public void UInt32_BoundaryValues(string text, uint expected)
    {
        Assert.Equal(new[] { expected }, (uint[])PlcValueCodec.Parse(PlcValueType.UInt32, text, EncodingChoice.Utf8));
    }

    [Fact]
    public void UInt32_OutOfRange_IsAFormatError()
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.UInt32, "4294967296", EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("0xFFFFFFFFFFFFFFFF", -1L)]
    public void Int64_BoundaryValues(string text, long expected)
    {
        Assert.Equal(new[] { expected }, (long[])PlcValueCodec.Parse(PlcValueType.Int64, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("0x10000000000000000")]
    public void Int64_OutOfRange_IsAFormatError(string text)
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Int64, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    [InlineData("0xFFFFFFFFFFFFFFFF", ulong.MaxValue)]
    public void UInt64_BoundaryValues(string text, ulong expected)
    {
        Assert.Equal(new[] { expected }, (ulong[])PlcValueCodec.Parse(PlcValueType.UInt64, text, EncodingChoice.Utf8));
    }

    [Fact]
    public void UInt64_OutOfRange_IsAFormatError()
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.UInt64, "18446744073709551616", EncodingChoice.Utf8));
    }

    [Fact]
    public void Float_AndDouble_ParseInvariantText_AndRoundTripThroughFormat()
    {
        Assert.Equal(new[] { 1.5f, -0.25f }, (float[])PlcValueCodec.Parse(PlcValueType.Float, "1.5, -0.25", EncodingChoice.Utf8));
        double[] values = (double[])PlcValueCodec.Parse(PlcValueType.Double, "-0.1", EncodingChoice.Utf8);
        Assert.Equal("-0.1", PlcValueCodec.Format(values));
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Double, "1,x", EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void Bool_AcceptsBitWords(string text, bool expected)
    {
        Assert.Equal(expected, PlcValueCodec.Parse(PlcValueType.Bool, text, EncodingChoice.Utf8));
    }

    [Fact]
    public void Bool_RejectsOtherWordsAndSeveralValues()
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Bool, "2", EncodingChoice.Utf8));
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Bool, "1,0", EncodingChoice.Utf8));
    }

    [Fact]
    public void Bits_ParseEachValue()
    {
        Assert.Equal(new[] { true, false, true }, (bool[])PlcValueCodec.Parse(PlcValueType.Bits, "1,0,true", EncodingChoice.Utf8));
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Bits, "1,2", EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("0", (byte)0)]
    [InlineData("255", (byte)255)]
    [InlineData("0x7F", (byte)0x7F)]
    public void Byte_BoundaryValues(string text, byte expected)
    {
        Assert.Equal(expected, PlcValueCodec.Parse(PlcValueType.Byte, text, EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("256")]
    [InlineData("1,2")]
    public void Byte_OutOfRangeOrSeveralValues_IsAFormatError(string text)
    {
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Byte, text, EncodingChoice.Utf8));
    }

    [Fact]
    public void Bytes_AndWords_UseTheByteInputSyntax()
    {
        Assert.Equal(new byte[] { 0x0A, 0xFF }, (byte[])PlcValueCodec.Parse(PlcValueType.Bytes, "hex:0A FF", EncodingChoice.Utf8));
        Assert.Equal(new byte[] { 0x12, 0x34 }, (byte[])PlcValueCodec.Parse(PlcValueType.Words, "hex:12 34", EncodingChoice.Utf8));
        Assert.Throws<FormatException>(() => PlcValueCodec.Parse(PlcValueType.Bytes, "hex:GG", EncodingChoice.Utf8));
    }

    [Fact]
    public void String_IsTakenWhole_WithoutSplittingOnCommas()
    {
        Assert.Equal("A,B C", PlcValueCodec.Parse(PlcValueType.String, "A,B C", EncodingChoice.Utf8));
    }

    [Fact]
    public void Format_JoinsArrays_AndWritesBitsAndBytesInTheirOwnStyle()
    {
        Assert.Equal("1, -2", PlcValueCodec.Format(new short[] { 1, -2 }));
        Assert.Equal("1, 0, 1", PlcValueCodec.Format(new[] { true, false, true }));
        Assert.Equal("true", PlcValueCodec.Format(true));
        Assert.Equal(HexFormatter.ToHex(new byte[] { 0x0A, 0xFF }), PlcValueCodec.Format(new byte[] { 0x0A, 0xFF }));
        Assert.Equal(string.Empty, PlcValueCodec.Format(null));
    }
}
