using Junevy.Communication.Plc;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 地址解析的成功路径（计划 Task 6、P4-D16）：四个区的全部写法、大小写与 <c>%</c> 前缀、取值边界，
/// 以及 <see cref="S7Address"/> 与 <see cref="S7Device"/> 的属性。
/// </summary>
public sealed class S7AddressTests
{
    [Theory]
    [InlineData("DB1.DBX12.3", S7Area.DataBlock, "DB", 1, 12, 3)]
    [InlineData("DB1.DBB12", S7Area.DataBlock, "DB", 1, 12, null)]
    [InlineData("DB1.DBW12", S7Area.DataBlock, "DB", 1, 12, null)]
    [InlineData("DB1.DBD12", S7Area.DataBlock, "DB", 1, 12, null)]
    [InlineData("M12.3", S7Area.Merkers, "M", 0, 12, 3)]
    [InlineData("MB12", S7Area.Merkers, "M", 0, 12, null)]
    [InlineData("MW12", S7Area.Merkers, "M", 0, 12, null)]
    [InlineData("MD12", S7Area.Merkers, "M", 0, 12, null)]
    [InlineData("I12.3", S7Area.Inputs, "I", 0, 12, 3)]
    [InlineData("IB12", S7Area.Inputs, "I", 0, 12, null)]
    [InlineData("IW12", S7Area.Inputs, "I", 0, 12, null)]
    [InlineData("ID12", S7Area.Inputs, "I", 0, 12, null)]
    [InlineData("Q12.3", S7Area.Outputs, "Q", 0, 12, 3)]
    [InlineData("QB12", S7Area.Outputs, "Q", 0, 12, null)]
    [InlineData("QW12", S7Area.Outputs, "Q", 0, 12, null)]
    [InlineData("QD12", S7Area.Outputs, "Q", 0, 12, null)]
    public void EverySpelling_ParsesToTheExpectedAreaAndNumbers(
        string text, S7Area area, string symbol, int dbNumber, int byteNumber, int? bitIndex)
    {
        Assert.True(S7AddressParser.TryParse(text, out S7Address? address, out string? error), error);

        Assert.Equal(area, address!.Device.Area);
        Assert.Equal(symbol, address.Device.Symbol);
        Assert.Equal(PlcAddressUnit.Byte, address.Unit);
        Assert.Equal(dbNumber, address.DbNumber);
        Assert.Equal(byteNumber, address.Number);
        Assert.Equal(bitIndex, address.BitIndex);
        Assert.Equal(text, address.Text);
    }

    [Theory]
    [InlineData("DB1.DBX12.3")]
    [InlineData("DB1.DBB12")]
    [InlineData("M12.3")]
    [InlineData("MB12")]
    [InlineData("IW12")]
    [InlineData("QD12")]
    public void CaseAndPercentPrefix_DoNotChangeTheResult(string canonical)
    {
        Assert.True(S7AddressParser.TryParse(canonical, out S7Address? expected, out string? error), error);

        string[] variants =
        {
            canonical.ToLowerInvariant(),
            "%" + canonical,
            "%" + canonical.ToLowerInvariant(),
            "  " + canonical + "  ",
        };

        foreach (string variant in variants)
        {
            Assert.True(S7AddressParser.TryParse(variant, out S7Address? actual, out string? variantError), variantError);

            Assert.Equal(expected!.Device.Area, actual!.Device.Area);
            Assert.Equal(expected.DbNumber, actual.DbNumber);
            Assert.Equal(expected.Number, actual.Number);
            Assert.Equal(expected.BitIndex, actual.BitIndex);
            Assert.Equal(variant.Trim(), actual.Text);
        }
    }

    [Theory]
    [InlineData("DB1.DBW0", 1, 0)]
    [InlineData("DB65535.DBW0", 65535, 0)]
    [InlineData("DB1.DBW2097151", 1, 2097151)]
    [InlineData("MB0", 0, 0)]
    [InlineData("MB2097151", 0, 2097151)]
    [InlineData("M2097151.7", 0, 2097151)]
    [InlineData("ID2097151", 0, 2097151)]
    [InlineData("QD2097151", 0, 2097151)]
    public void NumberRangeBoundaries_AreAccepted(string text, int dbNumber, int byteNumber)
    {
        Assert.True(S7AddressParser.TryParse(text, out S7Address? address, out string? error), error);

        Assert.Equal(dbNumber, address!.DbNumber);
        Assert.Equal(byteNumber, address.Number);
    }

    [Theory]
    [InlineData("M0.0", 0)]
    [InlineData("M0.7", 7)]
    [InlineData("I5.7", 7)]
    [InlineData("DB1.DBX0.0", 0)]
    [InlineData("DB1.DBX0.7", 7)]
    public void BitIndexBoundaries_AreAccepted(string text, int bitIndex)
    {
        Assert.True(S7AddressParser.TryParse(text, out S7Address? address, out string? error), error);

        Assert.Equal(bitIndex, address!.BitIndex);
    }

    [Fact]
    public void DataBlockAddress_ExposesItsAreaCodeNumbersAndText()
    {
        Assert.True(S7AddressParser.TryParse("DB3.DBX7.2", out S7Address? address, out string? error), error);

        S7Device device = address!.Device;
        Assert.Equal(S7Area.DataBlock, device.Area);
        Assert.Equal((byte)0x84, device.AreaCode);
        Assert.Equal("DB", device.Symbol);
        Assert.Equal(PlcAddressUnit.Byte, device.Unit);
        Assert.Equal(10, device.Radix);
        Assert.Equal(2097151, device.MaxNumber);
        Assert.Equal(3, address.DbNumber);
        Assert.Equal(7, address.Number);
        Assert.Equal(2, address.BitIndex);
        Assert.Equal("DB3.DBX7.2", address.ToString());
    }

    [Theory]
    [InlineData("I1.1", S7Area.Inputs, "I", 0x81)]
    [InlineData("Q1.1", S7Area.Outputs, "Q", 0x82)]
    [InlineData("M1.1", S7Area.Merkers, "M", 0x83)]
    [InlineData("DB1.DBW1", S7Area.DataBlock, "DB", 0x84)]
    public void EveryArea_ParsesToItsDeviceWithSymbolAndWireCode(string text, S7Area area, string symbol, int areaCode)
    {
        Assert.True(S7AddressParser.TryParse(text, out S7Address? address, out string? error), error);

        S7Device device = address!.Device;
        Assert.Equal(area, device.Area);
        Assert.Equal(symbol, device.Symbol);
        Assert.Equal((byte)areaCode, device.AreaCode);
        Assert.Equal(PlcAddressUnit.Byte, device.Unit);
        Assert.Equal(10, device.Radix);
        Assert.Equal(2097151, device.MaxNumber);
    }
}
