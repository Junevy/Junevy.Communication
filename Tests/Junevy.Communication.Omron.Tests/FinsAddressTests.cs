using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS 软元件表与地址解析（计划 Task 11、P4-D16）：18 个符号的单位、进制、编号上限与区域代码，以及解析规则的边界。
/// 区域代码取自计划第 17.3 节的码表；EM 存储区按组展开为 <c>E0_</c>–<c>EC_</c> 十三个符号。
/// </summary>
public sealed class FinsAddressTests
{
    // 每个符号一行：符号、字区代码、位区代码（17.3 节）。EM 的 n 组为 0xA0 + n / 0x20 + n。
    [Theory(Timeout = 20000)]
    [InlineData("CIO", 0xB0, 0x30)]
    [InlineData("W", 0xB1, 0x31)]
    [InlineData("H", 0xB2, 0x32)]
    [InlineData("A", 0xB3, 0x33)]
    [InlineData("D", 0x82, 0x02)]
    [InlineData("E0_", 0xA0, 0x20)]
    [InlineData("E1_", 0xA1, 0x21)]
    [InlineData("E2_", 0xA2, 0x22)]
    [InlineData("E3_", 0xA3, 0x23)]
    [InlineData("E4_", 0xA4, 0x24)]
    [InlineData("E5_", 0xA5, 0x25)]
    [InlineData("E6_", 0xA6, 0x26)]
    [InlineData("E7_", 0xA7, 0x27)]
    [InlineData("E8_", 0xA8, 0x28)]
    [InlineData("E9_", 0xA9, 0x29)]
    [InlineData("EA_", 0xAA, 0x2A)]
    [InlineData("EB_", 0xAB, 0x2B)]
    [InlineData("EC_", 0xAC, 0x2C)]
    public void EverySymbol_ParsesWithItsUnitRadixAndAreaCodes(string symbol, int wordAreaCode, int bitAreaCode)
    {
        Assert.True(FinsDeviceTable.AddressParser.TryParse(symbol + "1", out FinsAddress? address, out string? error), error);

        Assert.Equal(symbol, address!.Device.Symbol);
        Assert.Equal(PlcAddressUnit.Word, address.Unit);
        Assert.Equal(10, address.Device.Radix);
        Assert.Equal(65535, address.Device.MaxNumber);
        Assert.Equal((byte)wordAreaCode, address.Device.WordAreaCode);
        Assert.Equal((byte)bitAreaCode, address.Device.BitAreaCode);
        Assert.Equal(1, address.Number);
    }

    [Fact(Timeout = 20000)]
    public void DeviceTable_HasTheFiveFixedAreasAndThirteenEmGroups()
    {
        Assert.Equal(18, FinsDeviceTable.Devices.Count);
        Assert.Equal(
            new[] { "CIO", "W", "H", "A", "D", "E0_", "E1_", "E2_", "E3_", "E4_", "E5_", "E6_", "E7_", "E8_", "E9_", "EA_", "EB_", "EC_" },
            FinsDeviceTable.Devices.Select(device => device.Symbol).ToArray());
    }

    [Theory(Timeout = 20000)]
    [InlineData("W10.03", 10, 3)]
    [InlineData("W10.15", 10, 15)]
    [InlineData("W10.0", 10, 0)]
    [InlineData("W10", 10, null)]
    public void BitIndex_IsDecimalAndAllowsZeroToFifteen(string text, int number, int? bitIndex)
    {
        Assert.True(FinsDeviceTable.AddressParser.TryParse(text, out FinsAddress? address, out string? error), error);

        Assert.Equal(number, address!.Number);
        Assert.Equal(bitIndex, address.BitIndex);
    }

    [Theory(Timeout = 20000)]
    [InlineData("W10.16", "The bit index in address 'W10.16' must be between 0 and 15.")]
    [InlineData("W10.0A", "'W10.0A' is not a valid decimal bit index.")]
    public void BitIndex_AboveTheWordOrNotDecimal_Fails(string text, string expectedError)
    {
        Assert.False(FinsDeviceTable.AddressParser.TryParse(text, out _, out string? error));
        Assert.Equal(expectedError, error);
    }

    [Fact(Timeout = 20000)]
    public void EmGroups_AreDistinctAreasWithTheirOwnCodes()
    {
        Assert.True(FinsDeviceTable.AddressParser.TryParse("E0_100", out FinsAddress? first, out string? firstError), firstError);
        Assert.True(FinsDeviceTable.AddressParser.TryParse("EC_0", out FinsAddress? last, out string? lastError), lastError);

        Assert.Equal("E0_", first!.Device.Symbol);
        Assert.Equal(100, first.Number);
        Assert.Equal((byte)0xA0, first.Device.WordAreaCode);
        Assert.Equal("EC_", last!.Device.Symbol);
        Assert.Equal(0, last.Number);
        Assert.Equal((byte)0x2C, last.Device.BitAreaCode);
    }

    [Fact(Timeout = 20000)]
    public void GroupLetterBeyondC_IsAnUnknownSymbol()
    {
        Assert.False(FinsDeviceTable.AddressParser.TryParse("ED_0", out _, out string? error));
        Assert.Equal("Unknown device in address 'ED_0'.", error);
    }

    [Theory(Timeout = 20000)]
    [InlineData("C100")]
    [InlineData("Q10")]
    [InlineData("E100")]
    [InlineData("T10")]
    public void UnknownOrAbbreviatedSymbols_Fail(string text)
    {
        Assert.False(FinsDeviceTable.AddressParser.TryParse(text, out _, out string? error));
        Assert.Equal($"Unknown device in address '{text}'.", error);
    }

    [Fact(Timeout = 20000)]
    public void CioMustBeSpelledOut_WithItsOwnAreaCodes()
    {
        Assert.True(FinsDeviceTable.AddressParser.TryParse("CIO100", out FinsAddress? address, out string? error), error);

        Assert.Equal("CIO", address!.Device.Symbol);
        Assert.Equal(100, address.Number);
        Assert.Equal((byte)0xB0, address.Device.WordAreaCode);

        Assert.False(FinsDeviceTable.AddressParser.TryParse("CIO", out _, out string? missingError));
        Assert.Equal("The device number is missing in address 'CIO'.", missingError);
    }

    [Theory(Timeout = 20000)]
    [InlineData("w10.03", "W")]
    [InlineData("cio0", "CIO")]
    [InlineData("e0_1", "E0_")]
    [InlineData("  D5  ", "D")]
    public void Symbols_AreCaseInsensitiveAndTrimmed(string text, string symbol)
    {
        Assert.True(FinsDeviceTable.AddressParser.TryParse(text, out FinsAddress? address, out string? error), error);

        Assert.Equal(symbol, address!.Device.Symbol);
        Assert.Equal(text.Trim(), address.Text);
        Assert.Equal(text.Trim(), address.ToString());
    }

    [Fact(Timeout = 20000)]
    public void WordNumber_IsDecimalAndZeroToThePlcLimit()
    {
        Assert.True(FinsDeviceTable.AddressParser.TryParse("D0", out FinsAddress? zero, out string? zeroError), zeroError);
        Assert.True(FinsDeviceTable.AddressParser.TryParse("D65535", out FinsAddress? maximum, out string? maximumError), maximumError);
        Assert.Equal(0, zero!.Number);
        Assert.Equal(65535, maximum!.Number);

        Assert.False(FinsDeviceTable.AddressParser.TryParse("D65536", out _, out string? aboveError));
        Assert.Equal("The device number in address 'D65536' must be between 0 and 65535.", aboveError);

        Assert.False(FinsDeviceTable.AddressParser.TryParse("D1F", out _, out string? hexError));
        Assert.Equal("'D1F' is not a valid decimal device number.", hexError);
    }

    [Fact(Timeout = 20000)]
    public void EmptyAddress_Fails()
    {
        Assert.False(FinsDeviceTable.AddressParser.TryParse("   ", out _, out string? error));
        Assert.Equal("The address is empty.", error);
    }
}
