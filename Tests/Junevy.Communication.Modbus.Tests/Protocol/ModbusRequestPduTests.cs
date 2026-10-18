using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Protocol;

/// <summary>
/// PDU 请求编码（第 21.1 节 V-P 向量的请求部分），逐字节比对。
/// </summary>
public sealed class ModbusRequestPduTests
{
    private static byte[] Encode(Action<IBufferWriter<byte>> write)
    {
        using var buffer = new ByteBufferWriter();
        write(buffer);
        return buffer.ToArray();
    }

    private static void AssertEncodes(string expectedHex, Action<IBufferWriter<byte>> write)
        => Assert.Equal(HexVector.ToBytes(expectedHex), Encode(write));

    [Fact(Timeout = 20000)]
    public void ReadCoils_V_P_1() => AssertEncodes("01 00 13 00 13", output => ModbusRequestPdu.WriteReadRequest(output, 0x01, 19, 19));

    [Fact(Timeout = 20000)]
    public void ReadDiscreteInputs_V_P_2() => AssertEncodes("02 00 C4 00 16", output => ModbusRequestPdu.WriteReadRequest(output, 0x02, 196, 22));

    [Fact(Timeout = 20000)]
    public void ReadHoldingRegisters_V_P_3() => AssertEncodes("03 00 6B 00 03", output => ModbusRequestPdu.WriteReadRequest(output, 0x03, 107, 3));

    [Fact(Timeout = 20000)]
    public void ReadInputRegisters_V_P_4() => AssertEncodes("04 00 08 00 01", output => ModbusRequestPdu.WriteReadRequest(output, 0x04, 8, 1));

    [Fact(Timeout = 20000)]
    public void WriteSingleCoilOn_V_P_5() => AssertEncodes("05 00 AC FF 00", output => ModbusRequestPdu.WriteWriteSingleCoil(output, 172, true));

    [Fact(Timeout = 20000)]
    public void WriteSingleCoilOff_V_P_13() => AssertEncodes("05 00 00 00 00", output => ModbusRequestPdu.WriteWriteSingleCoil(output, 0, false));

    [Fact(Timeout = 20000)]
    public void WriteSingleRegister_V_P_6() => AssertEncodes("06 00 01 00 03", output => ModbusRequestPdu.WriteWriteSingleRegister(output, 1, 3));

    [Fact(Timeout = 20000)]
    public void WriteSingleRegister_V_P_15() => AssertEncodes("06 00 64 12 34", output => ModbusRequestPdu.WriteWriteSingleRegister(output, 100, 0x1234));

    [Fact(Timeout = 20000)]
    public void WriteMultipleCoils_PacksTheFirstPointInTheLowestBit_V_P_7()
    {
        bool[] values = { true, false, true, true, false, false, true, true, true, false };

        AssertEncodes("0F 00 13 00 0A 02 CD 01", output => ModbusRequestPdu.WriteWriteMultipleCoils(output, 19, values));
    }

    [Fact(Timeout = 20000)]
    public void WriteMultipleCoils_ThreePoints_V_P_14()
    {
        bool[] values = { true, false, true };

        AssertEncodes("0F 00 00 00 03 01 05", output => ModbusRequestPdu.WriteWriteMultipleCoils(output, 0, values));
    }

    [Fact(Timeout = 20000)]
    public void WriteMultipleRegisters_V_P_8() => AssertEncodes("10 00 01 00 02 04 00 0A 01 02",
        output => ModbusRequestPdu.WriteWriteMultipleRegisters(output, 1, HexVector.ToBytes("00 0A 01 02")));

    [Fact(Timeout = 20000)]
    public void MaskWriteRegister_V_P_9() => AssertEncodes("16 00 04 00 F2 00 25", output => ModbusRequestPdu.WriteMaskWriteRegister(output, 4, 0x00F2, 0x0025));

    [Fact(Timeout = 20000)]
    public void MaskWriteRegister_SetBit5_V_P_12() => AssertEncodes("16 00 64 FF DF 00 20", output => ModbusRequestPdu.WriteMaskWriteRegister(output, 100, 0xFFDF, 0x0020));

    [Fact(Timeout = 20000)]
    public void MaskWriteRegister_ClearBit5_V_P_12() => AssertEncodes("16 00 64 FF DF 00 00", output => ModbusRequestPdu.WriteMaskWriteRegister(output, 100, 0xFFDF, 0x0000));

    [Fact(Timeout = 20000)]
    public void ReadWriteMultipleRegisters_V_P_10() => AssertEncodes("17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF",
        output => ModbusRequestPdu.WriteReadWriteMultipleRegisters(output, 3, 6, 14, HexVector.ToBytes("00 FF 00 FF 00 FF")));
}
