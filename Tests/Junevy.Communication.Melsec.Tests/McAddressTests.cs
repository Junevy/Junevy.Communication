using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// MELSEC Q/L 兼容软元件表与地址解析（计划 Task 8）：表中每个符号的单位、进制与代码，以及解析规则的边界。
/// 代码与进制对照手册 p.67–68（8.1 节）的软元件代码表。
/// </summary>
public sealed class McAddressTests
{
    [Theory(Timeout = 20000)]
    [InlineData("SM", PlcAddressUnit.Bit, 10, "SM", 0x91)]
    [InlineData("SD", PlcAddressUnit.Word, 10, "SD", 0xA9)]
    [InlineData("X", PlcAddressUnit.Bit, 16, "X*", 0x9C)]
    [InlineData("Y", PlcAddressUnit.Bit, 16, "Y*", 0x9D)]
    [InlineData("M", PlcAddressUnit.Bit, 10, "M*", 0x90)]
    [InlineData("L", PlcAddressUnit.Bit, 10, "L*", 0x92)]
    [InlineData("F", PlcAddressUnit.Bit, 10, "F*", 0x93)]
    [InlineData("V", PlcAddressUnit.Bit, 10, "V*", 0x94)]
    [InlineData("B", PlcAddressUnit.Bit, 16, "B*", 0xA0)]
    [InlineData("D", PlcAddressUnit.Word, 10, "D*", 0xA8)]
    [InlineData("W", PlcAddressUnit.Word, 16, "W*", 0xB4)]
    [InlineData("TS", PlcAddressUnit.Bit, 10, "TS", 0xC1)]
    [InlineData("TC", PlcAddressUnit.Bit, 10, "TC", 0xC0)]
    [InlineData("TN", PlcAddressUnit.Word, 10, "TN", 0xC2)]
    [InlineData("SS", PlcAddressUnit.Bit, 10, "SS", 0xC7)]
    [InlineData("SC", PlcAddressUnit.Bit, 10, "SC", 0xC6)]
    [InlineData("SN", PlcAddressUnit.Word, 10, "SN", 0xC8)]
    [InlineData("CS", PlcAddressUnit.Bit, 10, "CS", 0xC4)]
    [InlineData("CC", PlcAddressUnit.Bit, 10, "CC", 0xC3)]
    [InlineData("CN", PlcAddressUnit.Word, 10, "CN", 0xC5)]
    [InlineData("SB", PlcAddressUnit.Bit, 16, "SB", 0xA1)]
    [InlineData("SW", PlcAddressUnit.Word, 16, "SW", 0xB5)]
    [InlineData("S", PlcAddressUnit.Bit, 10, "S*", 0x98)]
    [InlineData("DX", PlcAddressUnit.Bit, 16, "DX", 0xA2)]
    [InlineData("DY", PlcAddressUnit.Bit, 16, "DY", 0xA3)]
    [InlineData("Z", PlcAddressUnit.Word, 10, "Z*", 0xCC)]
    [InlineData("R", PlcAddressUnit.Word, 10, "R*", 0xAF)]
    [InlineData("ZR", PlcAddressUnit.Word, 16, "ZR", 0xB0)]
    public void EverySymbol_ParsesWithItsUnitRadixAndCodes(string symbol, PlcAddressUnit unit, int radix, string asciiCode, int binaryCode)
    {
        Assert.True(McDeviceTable.AddressParser.TryParse(symbol + "1", out McAddress? address, out string? error), error);

        Assert.Equal(symbol, address!.Device.Symbol);
        Assert.Equal(unit, address.Unit);
        Assert.Equal(radix, address.Device.Radix);
        Assert.Equal(asciiCode, address.Device.AsciiCode);
        Assert.Equal((byte)binaryCode, address.Device.BinaryCode);
        Assert.Equal(1, address.Number);
    }

    [Fact(Timeout = 20000)]
    public void EveryDevice_HasTheThreeByteNumberLimit()
    {
        Assert.Equal(28, McDeviceTable.Devices.Count);
        Assert.All(McDeviceTable.Devices, device => Assert.Equal(0xFFFFFF, device.MaxNumber));
    }

    [Fact(Timeout = 20000)]
    public void ZR1F_IsHexadecimal_ThirtyOne()
    {
        Assert.True(McDeviceTable.AddressParser.TryParse("ZR1F", out McAddress? address, out string? error), error);

        Assert.Equal("ZR", address!.Device.Symbol);
        Assert.Equal(31, address.Number);
    }

    [Fact(Timeout = 20000)]
    public void DX10_IsDirectInput_NotDataRegister()
    {
        Assert.True(McDeviceTable.AddressParser.TryParse("DX10", out McAddress? address, out string? error), error);

        Assert.Equal("DX", address!.Device.Symbol);
        Assert.Equal(PlcAddressUnit.Bit, address.Unit);
        Assert.Equal(16, address.Number);
    }

    [Fact(Timeout = 20000)]
    public void S10_AndSM10_AreDistinctDevices()
    {
        Assert.True(McDeviceTable.AddressParser.TryParse("S10", out McAddress? step, out string? stepError), stepError);
        Assert.True(McDeviceTable.AddressParser.TryParse("SM10", out McAddress? special, out string? specialError), specialError);

        Assert.Equal("S", step!.Device.Symbol);
        Assert.Equal("SM", special!.Device.Symbol);
        Assert.Equal(10, step.Number);
        Assert.Equal(10, special.Number);
    }

    [Fact(Timeout = 20000)]
    public void LowerCaseHexadecimalDigits_AreAccepted()
    {
        Assert.True(McDeviceTable.AddressParser.TryParse("x1a", out McAddress? address, out string? error), error);

        Assert.Equal("X", address!.Device.Symbol);
        Assert.Equal(0x1A, address.Number);
    }

    [Fact(Timeout = 20000)]
    public void BitIndexF_OnAWordDevice_IsFifteen()
    {
        Assert.True(McDeviceTable.AddressParser.TryParse("D100.F", out McAddress? address, out string? error), error);

        Assert.Equal(100, address!.Number);
        Assert.Equal(15, address.BitIndex);
    }

    [Fact(Timeout = 20000)]
    public void BitIndexOnABitDevice_Fails()
    {
        Assert.False(McDeviceTable.AddressParser.TryParse("M10.1", out _, out string? error));
        Assert.Equal("Bit devices do not support a bit index in address 'M10.1'.", error);
    }

    [Theory(Timeout = 20000)]
    [InlineData("D-1", "'D-1' is not a valid decimal device number.")]
    [InlineData("X1G", "'X1G' is not a valid hexadecimal device number.")]
    [InlineData("Q10", "Unknown device in address 'Q10'.")]
    public void InvalidAddresses_FailWithTheParserMessage(string text, string expectedError)
    {
        Assert.False(McDeviceTable.AddressParser.TryParse(text, out _, out string? error));
        Assert.Equal(expectedError, error);
    }

    [Fact(Timeout = 20000)]
    public void NumberAboveThreeBytes_Fails()
    {
        Assert.True(McDeviceTable.AddressParser.TryParse("D16777215", out McAddress? maximum, out string? maximumError), maximumError);
        Assert.Equal(16777215, maximum!.Number);

        Assert.False(McDeviceTable.AddressParser.TryParse("D16777216", out _, out string? error));
        Assert.Equal("The device number in address 'D16777216' must be between 0 and 16777215.", error);
    }

    [Fact(Timeout = 20000)]
    public void Address_ExposesTheMelsecDevice()
    {
        Assert.True(McDeviceTable.AddressParser.TryParse("D5", out McAddress? address, out string? error), error);

        McDevice device = address!.Device;
        Assert.Equal("D*", device.AsciiCode);
        Assert.Equal((byte)0xA8, device.BinaryCode);
    }
}
