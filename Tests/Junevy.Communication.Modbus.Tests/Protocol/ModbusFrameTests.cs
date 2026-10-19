using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Protocol;

/// <summary>
/// 帧（第 21.2 节 RTU、第 21.3 节 MBAP）的黄金向量：CRC、RTU 的封装与剥离、MBAP 的头部与校验。
/// 每条向量的编号写在用例名或 InlineData 中，便于对照计划。
/// </summary>
public sealed class ModbusFrameTests
{
    // 第 21.2 节的全部 RTU 整帧（含末尾 CRC，低字节在前）。
    public static IEnumerable<object[]> RtuFrames()
    {
        yield return new object[] { "V-R-1", "01 03 00 00 00 0A C5 CD" };
        yield return new object[] { "V-R-2", "01 03 00 00 00 01 84 0A" };
        yield return new object[] { "V-R-3", "01 03 02 00 01 79 84" };
        yield return new object[] { "V-R-4", "01 06 00 01 00 03 98 0B" };
        yield return new object[] { "V-R-5", "01 05 00 00 FF 00 8C 3A" };
        yield return new object[] { "V-R-6req", "01 03 00 6B 00 03 74 17" };
        yield return new object[] { "V-R-6resp", "01 03 06 02 2B 00 00 00 64 05 7A" };
        yield return new object[] { "V-R-7req", "01 01 00 13 00 13 8C 02" };
        yield return new object[] { "V-R-7resp", "01 01 03 CD 6B 05 42 82" };
        yield return new object[] { "V-R-8req", "01 02 00 C4 00 16 B8 39" };
        yield return new object[] { "V-R-8resp", "01 02 03 AC DB 35 22 88" };
        yield return new object[] { "V-R-9req", "01 04 00 08 00 01 B0 08" };
        yield return new object[] { "V-R-9resp", "01 04 02 00 0A 39 37" };
        yield return new object[] { "V-R-10", "01 05 00 AC FF 00 4C 1B" };
        yield return new object[] { "V-R-11req", "01 0F 00 13 00 0A 02 CD 01 72 CB" };
        yield return new object[] { "V-R-11resp", "01 0F 00 13 00 0A 24 09" };
        yield return new object[] { "V-R-12req", "01 10 00 01 00 02 04 00 0A 01 02 92 30" };
        yield return new object[] { "V-R-12resp", "01 10 00 01 00 02 10 08" };
        yield return new object[] { "V-R-13", "01 16 00 04 00 F2 00 25 67 EE" };
        yield return new object[] { "V-R-14req", "01 17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF 46 91" };
        yield return new object[] { "V-R-14resp", "01 17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF 1D 79" };
        yield return new object[] { "V-R-15", "01 83 02 C0 F1" };
        yield return new object[] { "V-R-16req", "02 03 00 64 00 02 85 E7" };
        yield return new object[] { "V-R-16resp", "02 03 04 12 34 56 78 B2 07" };
        yield return new object[] { "V-R-17set", "01 16 00 64 FF DF 00 20 87 F8" };
        yield return new object[] { "V-R-17clr", "01 16 00 64 FF DF 00 00 86 20" };
        yield return new object[] { "V-R-18write", "01 06 00 64 12 34 C5 62" };
        yield return new object[] { "V-R-18readreq", "01 03 00 64 00 01 C5 D5" };
        yield return new object[] { "V-R-18readresp", "01 03 02 12 34 B5 33" };
        yield return new object[] { "V-R-19off", "01 05 00 00 00 00 CD CA" };
        yield return new object[] { "V-R-19write3", "01 0F 00 00 00 03 01 05 4F 54" };
        yield return new object[] { "V-R-19readresp", "01 01 01 05 91 8B" };
        yield return new object[] { "V-R-20", "01 90 03 0C 01" };
    }

    [Theory(Timeout = 20000)]
    [MemberData(nameof(RtuFrames))]
    public void Crc_MatchesTheTrailingChecksumOfEveryRtuVector(string name, string frameHex)
    {
        byte[] frame = HexVector.ToBytes(frameHex);
        ushort expected = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));

        Assert.True(expected == ModbusCrc.Compute(frame.AsSpan(0, frame.Length - 2)), name);
    }

    [Theory(Timeout = 20000)]
    [InlineData(1, "03 00 00 00 0A", "01 03 00 00 00 0A C5 CD")]
    [InlineData(1, "03 00 00 00 01", "01 03 00 00 00 01 84 0A")]
    [InlineData(1, "06 00 01 00 03", "01 06 00 01 00 03 98 0B")]
    [InlineData(1, "05 00 00 FF 00", "01 05 00 00 FF 00 8C 3A")]
    [InlineData(1, "03 00 6B 00 03", "01 03 00 6B 00 03 74 17")]
    [InlineData(1, "01 00 13 00 13", "01 01 00 13 00 13 8C 02")]
    [InlineData(1, "0F 00 13 00 0A 02 CD 01", "01 0F 00 13 00 0A 02 CD 01 72 CB")]
    [InlineData(1, "10 00 01 00 02 04 00 0A 01 02", "01 10 00 01 00 02 04 00 0A 01 02 92 30")]
    [InlineData(1, "16 00 04 00 F2 00 25", "01 16 00 04 00 F2 00 25 67 EE")]
    [InlineData(1, "17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF", "01 17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF 46 91")]
    [InlineData(2, "03 00 64 00 02", "02 03 00 64 00 02 85 E7")]
    [InlineData(1, "16 00 64 FF DF 00 20", "01 16 00 64 FF DF 00 20 87 F8")]
    public void RtuWrite_WrapsThePduWithStationAndCrc(byte unitId, string pduHex, string frameHex)
    {
        using var output = new ByteBufferWriter();

        RtuAdu.Write(output, unitId, HexVector.ToBytes(pduHex));

        Assert.Equal(HexVector.ToBytes(frameHex), output.ToArray());
    }

    [Theory(Timeout = 20000)]
    [InlineData(1, "01 03 02 00 01 79 84", "03 02 00 01")]
    [InlineData(1, "01 03 06 02 2B 00 00 00 64 05 7A", "03 06 02 2B 00 00 00 64")]
    [InlineData(1, "01 01 03 CD 6B 05 42 82", "01 03 CD 6B 05")]
    [InlineData(1, "01 0F 00 13 00 0A 24 09", "0F 00 13 00 0A")]
    [InlineData(1, "01 83 02 C0 F1", "83 02")]
    [InlineData(1, "01 90 03 0C 01", "90 03")]
    [InlineData(2, "02 03 04 12 34 56 78 B2 07", "03 04 12 34 56 78")]
    public void RtuUnwrap_ReturnsThePduOfAValidFrame(byte unitId, string frameHex, string pduHex)
    {
        CommResult<byte[]> result = RtuAdu.Unwrap(HexVector.ToBytes(frameHex), unitId);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes(pduHex), result.Data);
    }

    [Fact(Timeout = 20000)]
    public void RtuUnwrap_WrongStation_IsProtocolViolation()
    {
        CommResult<byte[]> result = RtuAdu.Unwrap(HexVector.ToBytes("01 03 02 00 01 79 84"), 2);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void RtuUnwrap_CrcBitError_IsProtocolViolation()
    {
        byte[] frame = HexVector.Mutated("01 03 02 00 01 79 84", 3, 0x02);

        CommResult<byte[]> result = RtuAdu.Unwrap(frame, 1);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Theory(Timeout = 20000)]
    [InlineData(1, 1, "03 00 6B 00 03", "00 01 00 00 00 06 01 03 00 6B 00 03")]
    [InlineData(2, 1, "10 00 01 00 02 04 00 0A 01 02", "00 02 00 00 00 0B 01 10 00 01 00 02 04 00 0A 01 02")]
    [InlineData(4, 1, "01 00 13 00 13", "00 04 00 00 00 06 01 01 00 13 00 13")]
    [InlineData(4, 255, "01 00 13 00 13", "00 04 00 00 00 06 FF 01 00 13 00 13")]
    public void MbapWrite_WritesTheHeaderWithUnitAndLength(int transactionId, byte unitId, string pduHex, string frameHex)
    {
        using var output = new ByteBufferWriter();
        byte[] pdu = HexVector.ToBytes(pduHex);

        MbapHeader.Write(output, (ushort)transactionId, unitId, pdu.Length);
        BigEndianFields.WriteBytes(output, pdu);

        Assert.Equal(HexVector.ToBytes(frameHex), output.ToArray());
    }

    [Theory(Timeout = 20000)]
    [InlineData(1, "00 01 00 00 00 09 01 03 06 02 2B 00 00 00 64", "03 06 02 2B 00 00 00 64")]
    [InlineData(2, "00 02 00 00 00 06 01 10 00 01 00 02", "10 00 01 00 02")]
    [InlineData(3, "00 03 00 00 00 03 01 83 02", "83 02")]
    [InlineData(4, "00 04 00 00 00 06 FF 01 03 CD 6B 05", "01 03 CD 6B 05")]
    public void MbapUnwrap_ReturnsThePduOfAValidFrame(int transactionId, string frameHex, string pduHex)
    {
        byte[] frame = HexVector.ToBytes(frameHex);
        byte unitId = frame[6];

        CommResult<byte[]> result = MbapHeader.Unwrap(frame, unitId);

        Assert.True(result.IsSuccess, $"transaction {transactionId}");
        Assert.Equal(HexVector.ToBytes(pduHex), result.Data);
    }

    [Fact(Timeout = 20000)]
    public void MbapUnwrap_NonZeroProtocolIdentifier_IsProtocolViolation()
    {
        CommResult<byte[]> result = MbapHeader.Unwrap(HexVector.ToBytes("00 05 00 01 00 05 01 03 02 00 01"), 1);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void MbapUnwrap_LengthFieldMismatch_IsProtocolViolation()
    {
        byte[] frame = HexVector.Mutated("00 01 00 00 00 09 01 03 06 02 2B 00 00 00 64", 5, 0x0A);

        CommResult<byte[]> result = MbapHeader.Unwrap(frame, 1);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void MbapUnwrap_WrongUnitId_IsProtocolViolation()
    {
        CommResult<byte[]> result = MbapHeader.Unwrap(HexVector.ToBytes("00 01 00 00 00 09 01 03 06 02 2B 00 00 00 64"), 2);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void MbapUnwrap_ShorterThanHeader_IsProtocolViolation()
    {
        CommResult<byte[]> result = MbapHeader.Unwrap(HexVector.Truncated("00 01 00 00 00 09 01", 5), 1);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public void MbapUnwrap_DoesNotCheckTheTransactionId()
    {
        // 事务号由通道按键匹配（P7-D10），解析时不再检查。
        CommResult<byte[]> result = MbapHeader.Unwrap(HexVector.ToBytes("00 77 00 00 00 06 01 03 00 6B 00 03"), 1);

        Assert.True(result.IsSuccess);
    }

    [Fact(Timeout = 20000)]
    public void TransactionIds_AreDistinctAcrossCalls()
    {
        ushort first = MbapHeader.TransactionIds.NextUInt16();
        ushort second = MbapHeader.TransactionIds.NextUInt16();

        Assert.NotEqual(first, second);
    }
}
