using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Protocol;

/// <summary>
/// PDU 应答解码（第 21.1 节 V-P 向量的应答部分）：位与字的解码、回显校验、异常应答、字节数与功能码校验，以及位打包的位序。
/// </summary>
public sealed class ModbusResponsePduTests
{
    private static ModbusExpectation Read(byte functionCode, int count) => new ModbusExpectation(1, functionCode, count, null);

    private static ModbusExpectation Echo(byte functionCode, string echoHex) => new ModbusExpectation(1, functionCode, 0, HexVector.ToBytes(echoHex));

    [Fact(Timeout = 20000)]
    public void ReadCoils_V_P_1_UnpacksTheLowestBitFirst()
    {
        CommResult<bool[]> result = ModbusResponsePdu.DecodeBits(HexVector.ToBytes("01 03 CD 6B 05"), Read(0x01, 19));

        Assert.True(result.IsSuccess);
        bool[] expected =
        {
            true, false, true, true, false, false, true, true,
            true, true, false, true, false, true, true, false,
            true, false, true,
        };
        Assert.Equal(expected, result.Data);
    }

    [Fact(Timeout = 20000)]
    public void ReadDiscreteInputs_V_P_2_UnpacksTwentyTwoPoints()
    {
        CommResult<bool[]> result = ModbusResponsePdu.DecodeBits(HexVector.ToBytes("02 03 AC DB 35"), Read(0x02, 22));

        Assert.True(result.IsSuccess);
        bool[] expected =
        {
            false, false, true, true, false, true, false, true,
            true, true, false, true, true, false, true, true,
            true, false, true, false, true, true,
        };
        Assert.Equal(expected, result.Data);
    }

    [Fact(Timeout = 20000)]
    public void ReadHoldingRegisters_V_P_3_ReturnsTheRegisterBytes()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("03 06 02 2B 00 00 00 64"), Read(0x03, 3));

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("02 2B 00 00 00 64"), result.Data);
    }

    [Fact(Timeout = 20000)]
    public void ReadInputRegisters_V_P_4_ReturnsTheRegisterBytes()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("04 02 00 0A"), Read(0x04, 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("00 0A"), result.Data);
    }

    [Fact(Timeout = 20000)]
    public void ReadWriteMultipleRegisters_V_P_10_ReturnsOnlyTheReadBytes()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF"), Read(0x17, 6));

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("00 FE 0A CD 00 01 00 03 00 0D 00 FF"), result.Data);
    }

    [Theory(Timeout = 20000)]
    [InlineData("05 00 AC FF 00", 0x05, "00 AC FF 00")]
    [InlineData("06 00 01 00 03", 0x06, "00 01 00 03")]
    [InlineData("0F 00 13 00 0A", 0x0F, "00 13 00 0A")]
    [InlineData("10 00 01 00 02", 0x10, "00 01 00 02")]
    [InlineData("16 00 04 00 F2 00 25", 0x16, "00 04 00 F2 00 25")]
    [InlineData("05 00 00 00 00", 0x05, "00 00 00 00")]
    [InlineData("0F 00 00 00 03", 0x0F, "00 00 00 03")]
    [InlineData("06 00 64 12 34", 0x06, "00 64 12 34")]
    [InlineData("16 00 64 FF DF 00 20", 0x16, "00 64 FF DF 00 20")]
    public void WriteEcho_MatchingEcho_IsSuccess(string responseHex, byte functionCode, string echoHex)
    {
        CommResult result = ModbusResponsePdu.DecodeEcho(HexVector.ToBytes(responseHex), Echo(functionCode, echoHex));

        Assert.True(result.IsSuccess);
    }

    [Theory(Timeout = 20000)]
    [InlineData("05 00 AC 00 00", 0x05, "00 AC FF 00", "echoed value")]
    [InlineData("06 00 02 00 03", 0x06, "00 01 00 03", "echoed value")]
    [InlineData("0F 00 13 00 0B", 0x0F, "00 13 00 0A", "echoed value")]
    [InlineData("16 00 04 00 F3 00 25", 0x16, "00 04 00 F2 00 25", "echoed value")]
    [InlineData("06 00 01 00", 0x06, "00 01 00 03", "length")]
    public void WriteEcho_MismatchedEcho_IsProtocolViolation(string responseHex, byte functionCode, string echoHex, string keyword)
    {
        CommResult result = ModbusResponsePdu.DecodeEcho(HexVector.ToBytes(responseHex), Echo(functionCode, echoHex));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Contains(keyword, result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public void ExceptionResponse_IsRemoteErrorWithTheNamedExceptionCode()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("83 02"), Read(0x03, 3));

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(2, result.ProtocolErrorCode);
        Assert.Equal("Modbus exception 0x02 (Illegal Data Address) for function 0x03.", result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public void ExceptionResponse_ForAWriteFunction_NamesTheRequestFunction()
    {
        CommResult result = ModbusResponsePdu.DecodeEcho(HexVector.ToBytes("90 03"), Echo(0x10, "00 01 00 02"));

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(3, result.ProtocolErrorCode);
        Assert.Equal("Modbus exception 0x03 (Illegal Data Value) for function 0x10.", result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public void ExceptionResponse_WithAnUnlistedCode_HasNoName()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("83 07"), Read(0x03, 1));

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("Modbus exception 0x07 for function 0x03.", result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public void ExceptionResponse_ForAnotherFunction_IsProtocolViolation()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("84 02"), Read(0x03, 1));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void ExceptionResponse_WithExtraBytes_IsProtocolViolation()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("83 02 00"), Read(0x03, 1));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void NormalResponse_ForAnotherFunction_IsProtocolViolation()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("04 02 00 0A"), Read(0x03, 1));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void ReadWords_ByteCountMismatch_IsProtocolViolation()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("03 04 00 01 00 02"), Read(0x03, 3));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Contains("expected 6", result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public void ReadBits_ByteCountMismatch_IsProtocolViolation()
    {
        CommResult<bool[]> result = ModbusResponsePdu.DecodeBits(HexVector.ToBytes("01 02 CD 6B"), Read(0x01, 19));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void ReadWords_TruncatedResponse_IsProtocolViolation()
    {
        CommResult<byte[]> result = ModbusResponsePdu.DecodeWords(HexVector.ToBytes("03"), Read(0x03, 1));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void ExceptionCodes_NameTheNineStandardCodes()
    {
        Assert.Equal("Illegal Function", ModbusExceptionCodes.NameOf(0x01));
        Assert.Equal("Illegal Data Address", ModbusExceptionCodes.NameOf(0x02));
        Assert.Equal("Illegal Data Value", ModbusExceptionCodes.NameOf(0x03));
        Assert.Equal("Server Device Failure", ModbusExceptionCodes.NameOf(0x04));
        Assert.Equal("Acknowledge", ModbusExceptionCodes.NameOf(0x05));
        Assert.Equal("Server Device Busy", ModbusExceptionCodes.NameOf(0x06));
        Assert.Equal("Memory Parity Error", ModbusExceptionCodes.NameOf(0x08));
        Assert.Equal("Gateway Path Unavailable", ModbusExceptionCodes.NameOf(0x0A));
        Assert.Equal("Gateway Target Device Failed to Respond", ModbusExceptionCodes.NameOf(0x0B));
        Assert.Null(ModbusExceptionCodes.NameOf(0x07));
    }

    [Fact(Timeout = 20000)]
    public void Bits_PackAndUnpackUseTheLowestBitFirst()
    {
        bool[] values = { true, false, true, true, false, false, true, true, true, false };
        byte[] packed = new byte[2];

        ModbusBits.Pack(values, packed);

        Assert.Equal(HexVector.ToBytes("CD 01"), packed);
        Assert.Equal(values, ModbusBits.Unpack(packed, values.Length));
    }
}
