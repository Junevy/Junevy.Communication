using Junevy.Communication.Siemens.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 应答的帧结构错误（计划 Task 7 第 4 条）：长度字段不一致、截断、ROSCTR 错误、引用号不符，
/// 以及分帧之下每一层（TPKT、COTP DT、S7 应答头、参数块、数据项）结构不符时都返回 <c>ProtocolViolation</c>。
/// 违规帧都由 17.1 节的合法向量派生（<see cref="HexVector.Mutated"/> 或 <see cref="HexVector.Truncated"/>）。
/// </summary>
public sealed class S7ProtocolViolationTests
{
    private const ushort ReadDbReference = 0x0002;
    private const ushort WriteReference = 0x0003;
    private const ushort MerkerReference = 0x0005;
    private const ushort SetupReference = 0x0001;
    private const ushort HeaderErrorReference = 0x0007;

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(18)]
    public void FrameShorterThanATpktPlusCotpDtPlusAckHeader_IsViolation(int length)
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Truncated(V_S7_12, length), HeaderErrorReference, 4));
    }

    [Fact]
    public void TpktVersionOtherThan3_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, TpktVersion, 0x02), ReadDbReference, 4));
    }

    [Fact]
    public void TpktLengthNotEqualToFrameLength_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, TpktLengthLow, 0x1C), ReadDbReference, 4));
    }

    [Fact]
    public void TruncatedFrame_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Truncated(V_S7_6, 28), ReadDbReference, 4));
    }

    [Fact]
    public void CotpLengthOtherThan2_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, CotpLength, 0x03), ReadDbReference, 4));
    }

    [Fact]
    public void CotpPduTypeOtherThanDataTransfer_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, CotpPduType, 0xE0), ReadDbReference, 4));
    }

    [Fact]
    public void ProtocolIdOtherThan32_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, ProtocolId, 0x31), ReadDbReference, 4));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x04)]
    public void RosctrOtherThanAckOrAckData_IsViolation(int rosctr)
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, Rosctr, (byte)rosctr), ReadDbReference, 4));
    }

    [Fact]
    public void PduReferenceDifferentFromTheRequest_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(ToBytes(V_S7_6), WriteReference, 4));
    }

    [Theory]
    [InlineData(ParameterLengthLow)]
    [InlineData(DataLengthLow)]
    public void HeaderLengthFieldsNotMatchingTheFrame_AreViolation(int lengthIndex)
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, lengthIndex, 0x03), ReadDbReference, 4));
    }

    [Fact]
    public void ReadParametersLongerThanTwoBytes_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(ToBytes(Read_Ack_LongParameters), ReadDbReference, 4));
    }

    [Fact]
    public void ReadParametersOfAnotherService_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, ResponseFunction, 0x05), ReadDbReference, 4));
    }

    [Fact]
    public void ReadParametersCarryingTwoItems_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, ItemCount, 0x02), ReadDbReference, 4));
    }

    [Fact]
    public void ReadItemWithAnUndocumentedTransportSize_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, TransportSize, 0x05), ReadDbReference, 4));
    }

    [Fact]
    public void ReadItemWithBitLengthNotWholeBytes_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(Mutated(V_S7_6, UnitLengthLow, 0x09), ReadDbReference, 4));
    }

    [Fact]
    public void ReadItemWhoseLengthIsNotTheRequestedByteCount_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(ToBytes(V_S7_6), ReadDbReference, 3));
    }

    [Fact]
    public void ReadItemWithFewerBytesThanItsLengthDeclares_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(ToBytes(Read_Ack_ShortPayload), MerkerReference, 4));
    }

    [Fact]
    public void ReadItemWithTrailingBytes_IsViolation()
    {
        AssertViolation<byte[]>(S7ReadVar.ReadResponse(ToBytes(Read_Ack_TrailingBytes), ReadDbReference, 4));
    }

    [Fact]
    public void WriteAckWithoutReturnCode_IsViolation()
    {
        AssertViolation(S7WriteVar.ReadResponse(ToBytes(Write_Ack_WithoutReturnCode), WriteReference));
    }

    [Fact]
    public void WriteAckOfAnotherService_IsViolation()
    {
        AssertViolation(S7WriteVar.ReadResponse(Mutated(V_S7_8, ResponseFunction, 0x04), WriteReference));
    }

    [Fact]
    public void SetupAckWithShortParameters_IsViolation()
    {
        AssertViolation<int>(S7SetupCommunication.ReadNegotiatedPdu(ToBytes(Setup_Ack_ShortParameters), SetupReference));
    }

    [Fact]
    public void SetupAckWithoutTheResourceParameter_IsViolation()
    {
        AssertViolation<int>(S7SetupCommunication.ReadNegotiatedPdu(ToBytes(Setup_Ack_OtherParameter), SetupReference));
    }

    [Theory]
    [InlineData(0x3F)]
    [InlineData(0x00)]
    public void NegotiatedPduSmallerThan64_IsViolation(int lowByte)
    {
        // P4-D13：协商结果小于 64 时握手失败，返回 ProtocolViolation。
        AssertViolation<int>(S7SetupCommunication.ReadNegotiatedPdu(Mutated(V_S7_4, NegotiatedPduLow, (byte)lowByte), SetupReference));
    }

    [Fact]
    public void ConfirmWithWrongTpktLength_IsViolation()
    {
        AssertViolation(CotpFrames.ReadConnectionConfirm(Truncated(V_S7_2, 21)));
    }
}
