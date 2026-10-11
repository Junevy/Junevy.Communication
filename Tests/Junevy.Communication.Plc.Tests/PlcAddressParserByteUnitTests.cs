namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 字节软元件的地址规则（计划 P4-D3 的 S1、S2）：位号上限由软元件单位决定，字软元件 15、字节软元件 7，位软元件仍不带位号。
/// </summary>
public sealed class PlcAddressParserByteUnitTests
{
    private static readonly TestDevice[] Devices =
    {
        new TestDevice("B", PlcAddressUnit.Byte, 10, 9999),
        new TestDevice("D", PlcAddressUnit.Word, 10, 9999),
        new TestDevice("M", PlcAddressUnit.Bit, 10, 9999),
    };

    [Theory]
    [InlineData("B10.0", 0)]
    [InlineData("B10.4", 4)]
    [InlineData("B10.7", 7)]
    public void TryParse_BitIndexUpToSeven_IsAcceptedOnByteDevices(string text, int bitIndex)
    {
        Assert.True(Parse(text, out TestAddress? address, out string? error), error);
        Assert.Equal(PlcAddressUnit.Byte, address!.Unit);
        Assert.Equal(10, address.Number);
        Assert.Equal(bitIndex, address.BitIndex);
    }

    [Theory]
    [InlineData("B10.8")]
    [InlineData("B10.15")]
    public void TryParse_BitIndexAboveSeven_FailsWithTheByteLimitInTheMessage(string text)
    {
        Assert.False(Parse(text, out TestAddress? address, out string? error));
        Assert.Null(address);
        Assert.Equal($"The bit index in address '{text}' must be between 0 and 7.", error);
    }

    [Fact]
    public void TryParse_WithoutBitIndex_LeavesBitIndexNullOnByteDevices()
    {
        Assert.True(Parse("B10", out TestAddress? address, out string? error), error);
        Assert.Null(address!.BitIndex);
    }

    [Fact]
    public void TryParse_WordDevices_KeepTheirOwnBitLimit()
    {
        Assert.True(Parse("D100.15", out _, out string? error), error);
        Assert.Equal("The bit index in address 'D100.16' must be between 0 and 15.", ParseError("D100.16"));
    }

    [Fact]
    public void TryParse_BitDevices_StillRejectABitIndex()
    {
        Assert.Equal("Bit devices do not support a bit index in address 'M10.1'.", ParseError("M10.1"));
    }

    private static bool Parse(string text, out TestAddress? address, out string? error)
        => CreateParser().TryParse(text, out address, out error);

    private static string ParseError(string text)
    {
        Parse(text, out _, out string? error);
        return error!;
    }

    private static PlcAddressParser<TestDevice, TestAddress> CreateParser()
        => new PlcAddressParser<TestDevice, TestAddress>(
            Devices, (device, number, bitIndex, text) => new TestAddress(device, number, bitIndex, text));
}
