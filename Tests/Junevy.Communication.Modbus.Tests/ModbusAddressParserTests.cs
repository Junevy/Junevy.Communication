using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 地址解析（P7-D4）：符号写法（零基）、Modicon 写法（一基）、位号与站号前缀；非法写法返回能识别问题的错误消息。
/// </summary>
public sealed class ModbusAddressParserTests
{
    [Theory(Timeout = 20000)]
    [InlineData("HR0", "HR", 0, null, null)]
    [InlineData("hr65535", "HR", 65535, null, null)]
    [InlineData("C0", "C", 0, null, null)]
    [InlineData("DI100", "DI", 100, null, null)]
    [InlineData("IR7", "IR", 7, null, null)]
    [InlineData("HR100.15", "HR", 100, 15, null)]
    [InlineData("00001", "C", 0, null, null)]
    [InlineData("10001", "DI", 0, null, null)]
    [InlineData("30001", "IR", 0, null, null)]
    [InlineData("40001", "HR", 0, null, null)]
    [InlineData("49999", "HR", 9998, null, null)]
    [InlineData("400001", "HR", 0, null, null)]
    [InlineData("465536", "HR", 65535, null, null)]
    [InlineData("40001.3", "HR", 0, 3, null)]
    [InlineData("s=2;HR100", "HR", 100, null, 2)]
    [InlineData("S=0;40001", "HR", 0, null, 0)]
    [InlineData("s=255;C0", "C", 0, null, 255)]
    public void TryParse_ValidAddress_ReturnsTheDeviceNumberBitAndUnitId(string text, string symbol, int number, int? bitIndex, int? unitId)
    {
        bool ok = ModbusAddressParser.TryParse(text, out ModbusAddress? address, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(address);
        Assert.Equal(symbol, address!.Device.Symbol);
        Assert.Equal(number, address.Number);
        Assert.Equal(bitIndex, address.BitIndex);
        Assert.Equal((byte?)unitId, address.UnitId);
    }

    [Theory(Timeout = 20000)]
    [InlineData("HR65536", "between")]
    [InlineData("40000", "Modicon")]
    [InlineData("465537", "Modicon")]
    [InlineData("20001", "Modicon")]
    [InlineData("50001", "Modicon")]
    [InlineData("100", "Modicon")]
    [InlineData("4000001", "Modicon")]
    [InlineData("C1.0", "bit index")]
    [InlineData("DI1.0", "bit index")]
    [InlineData("HR1.16", "bit index")]
    [InlineData("00001.0", "bit index")]
    [InlineData("s=256;HR1", "unit ID")]
    [InlineData("s=;HR1", "unit ID")]
    [InlineData("s=2HR1", "';'")]
    [InlineData("s=2;", "after the unit ID")]
    [InlineData("X100", "Unknown device")]
    [InlineData("", "empty")]
    public void TryParse_InvalidAddress_ReturnsAnErrorNamingTheProblem(string text, string keyword)
    {
        bool ok = ModbusAddressParser.TryParse(text, out ModbusAddress? address, out string? error);

        Assert.False(ok);
        Assert.Null(address);
        Assert.NotNull(error);
        Assert.Contains(keyword, error!);
    }

    [Fact(Timeout = 20000)]
    public void TryParse_KeepsTheTrimmedTextAsTheAddressText()
    {
        ModbusAddressParser.TryParse("  s=2;HR100  ", out ModbusAddress? address, out _);

        Assert.Equal("s=2;HR100", address!.Text);
    }

    [Fact(Timeout = 20000)]
    public void StandardDevices_HaveTheSymbolUnitAndAccessOfTheStandardAreas()
    {
        Assert.Equal(("C", PlcAddressUnit.Bit, false), (ModbusDevice.Coil.Symbol, ModbusDevice.Coil.Unit, ModbusDevice.Coil.IsReadOnly));
        Assert.Equal(("DI", PlcAddressUnit.Bit, true), (ModbusDevice.DiscreteInput.Symbol, ModbusDevice.DiscreteInput.Unit, ModbusDevice.DiscreteInput.IsReadOnly));
        Assert.Equal(("IR", PlcAddressUnit.Word, true), (ModbusDevice.InputRegister.Symbol, ModbusDevice.InputRegister.Unit, ModbusDevice.InputRegister.IsReadOnly));
        Assert.Equal(("HR", PlcAddressUnit.Word, false), (ModbusDevice.HoldingRegister.Symbol, ModbusDevice.HoldingRegister.Unit, ModbusDevice.HoldingRegister.IsReadOnly));
    }

    [Fact(Timeout = 20000)]
    public void StandardDevices_UseDecimalNumbersUpTo65535()
    {
        foreach (ModbusDevice device in new[] { ModbusDevice.Coil, ModbusDevice.DiscreteInput, ModbusDevice.InputRegister, ModbusDevice.HoldingRegister })
        {
            Assert.Equal(10, device.Radix);
            Assert.Equal(65535, device.MaxNumber);
        }
    }
}
