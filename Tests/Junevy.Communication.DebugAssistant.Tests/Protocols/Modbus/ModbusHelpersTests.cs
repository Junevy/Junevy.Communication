using Junevy.Communication.DebugAssistant.Protocols.Modbus;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.Modbus;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Modbus;

/// <summary>Modbus 页签的纯逻辑（计划 Task 8 第 3 条）：五种传输与帧格式组合的映射、掩码助手的向量、功能码提示与单个写入功能码的一致性。</summary>
public sealed class ModbusHelpersTests
{
    [Theory]
    [InlineData(PlcTransport.Tcp, ModbusFrameChoice.Auto, "Modbus TCP", ModbusFraming.Mbap)]
    [InlineData(PlcTransport.Tcp, ModbusFrameChoice.Rtu, "RTU over TCP", ModbusFraming.Rtu)]
    [InlineData(PlcTransport.Udp, ModbusFrameChoice.Auto, "Modbus UDP", ModbusFraming.Mbap)]
    [InlineData(PlcTransport.Udp, ModbusFrameChoice.Rtu, "RTU over UDP", ModbusFraming.Rtu)]
    [InlineData(PlcTransport.Serial, ModbusFrameChoice.Auto, "Modbus RTU", ModbusFraming.Rtu)]
    [InlineData(PlcTransport.Serial, ModbusFrameChoice.Rtu, "Modbus RTU", ModbusFraming.Rtu)]
    public void EachValidCombination_HasItsName_AndTheFramingItUses(PlcTransport transport, ModbusFrameChoice choice, string name, ModbusFraming framing)
    {
        Assert.Null(ModbusCombination.Problem(transport, choice));
        Assert.Equal(name, ModbusCombination.Name(transport, choice));
        Assert.Equal(framing, ModbusCombination.Resolve(transport, choice));
    }

    [Fact]
    public void SerialWithMbap_IsNotAValidCombination_AndExplainsWhy()
    {
        Assert.Null(ModbusCombination.Name(PlcTransport.Serial, ModbusFrameChoice.Mbap));
        Assert.Contains("RTU", ModbusCombination.Problem(PlcTransport.Serial, ModbusFrameChoice.Mbap));
    }

    [Theory]
    [InlineData(0, 0xFFFE, 0x0001)]
    [InlineData(5, 0xFFDF, 0x0020)]
    [InlineData(15, 0x7FFF, 0x8000)]
    public void SetBit_GivesTheAndAndOrMasks(int bit, ushort andMask, ushort orMask)
    {
        Assert.Equal((andMask, orMask), ModbusMask.SetBit(bit));
    }

    [Theory]
    [InlineData(0, 0xFFFE, 0x0000)]
    [InlineData(5, 0xFFDF, 0x0000)]
    [InlineData(15, 0x7FFF, 0x0000)]
    public void ClearBit_GivesTheAndAndOrMasks(int bit, ushort andMask, ushort orMask)
    {
        Assert.Equal((andMask, orMask), ModbusMask.ClearBit(bit));
    }

    [Theory]
    [InlineData(0x1234, 5, true, 0x1234)]
    [InlineData(0x1238, 3, false, 0x1230)]
    [InlineData(0x0000, 15, true, 0x8000)]
    public void TheMasks_ApplyTheDeviceFormula(ushort current, int bit, bool set, ushort expected)
    {
        (ushort andMask, ushort orMask) = set ? ModbusMask.SetBit(bit) : ModbusMask.ClearBit(bit);

        ushort result = (ushort)((current & andMask) | (orMask & ~andMask));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void TheBitNumber_MustBeFromZeroToFifteen(int bit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusMask.SetBit(bit));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusMask.ClearBit(bit));
    }

    [Fact]
    public void SingleWriteFunctions_FollowTheOption_ForOnePoint()
    {
        Assert.Equal(new[] { (1, (byte)0x06) }, ModbusFunctionHint.WritePackets("HR", 1, useSingleWriteFunctions: true));
        Assert.Equal(new[] { (1, (byte)0x10) }, ModbusFunctionHint.WritePackets("HR", 1, useSingleWriteFunctions: false));
        Assert.Equal(new[] { (1, (byte)0x05) }, ModbusFunctionHint.WritePackets("C", 1, useSingleWriteFunctions: true));
        Assert.Equal(new[] { (1, (byte)0x0F) }, ModbusFunctionHint.WritePackets("C", 1, useSingleWriteFunctions: false));
    }

    [Fact]
    public void LargeWrites_SplitIntoPackets_AndTheLastSinglePointPacketUsesTheSingleFunction()
    {
        Assert.Equal(new[] { (123, (byte)0x10), (1, (byte)0x06) }, ModbusFunctionHint.WritePackets("HR", 124, useSingleWriteFunctions: true));
        Assert.Equal(new[] { (123, (byte)0x10), (1, (byte)0x10) }, ModbusFunctionHint.WritePackets("HR", 124, useSingleWriteFunctions: false));
        Assert.Equal(new[] { (1968, (byte)0x0F), (1, (byte)0x05) }, ModbusFunctionHint.WritePackets("C", 1969, useSingleWriteFunctions: true));
    }

    [Fact]
    public void ReadOnlyAreas_HaveNoWritePackets_AndTheHintSaysSo()
    {
        Assert.Empty(ModbusFunctionHint.WritePackets("DI", 3, useSingleWriteFunctions: true));
        Assert.Empty(ModbusFunctionHint.WritePackets("IR", 3, useSingleWriteFunctions: true));
        Assert.Contains("写入会被拒绝", ModbusFunctionHint.Describe("IR", 3, useSingleWriteFunctions: true));
        Assert.Equal((byte?)0x04, ModbusFunctionHint.ReadFunction("IR"));
        Assert.Equal((byte?)0x02, ModbusFunctionHint.ReadFunction("DI"));
    }

    [Fact]
    public void TheHint_AgreesWithTheOption()
    {
        string single = ModbusFunctionHint.Describe("HR", 1, useSingleWriteFunctions: true);
        string multiple = ModbusFunctionHint.Describe("HR", 1, useSingleWriteFunctions: false);

        Assert.Contains("06", single);
        Assert.DoesNotContain("10", single.Replace("0x10", string.Empty));
        Assert.Contains("10", multiple);
        Assert.DoesNotContain("06", multiple);
    }
}
