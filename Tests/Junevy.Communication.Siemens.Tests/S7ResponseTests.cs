using Junevy.Communication.Siemens.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 应答解码（计划 Task 7 第 2、3 条）：第 17.1 节 V-S7-2、V-S7-4、V-S7-6、V-S7-8、V-S7-10、V-S7-11、V-S7-12 的解码结果，
/// 17.3 节的传输尺寸与返回码/错误类名称，以及 P4-D14 的固定消息与原始码。
/// </summary>
public sealed class S7ResponseTests
{
    private const ushort SetupReference = 0x0001;
    private const ushort ReadDbReference = 0x0002;
    private const ushort WriteReference = 0x0003;
    private const ushort ReadMerkerReference = 0x0005;
    private const ushort ItemErrorReference = 0x0006;
    private const ushort HeaderErrorReference = 0x0007;

    [Fact]
    public void V_S7_2_ConnectionConfirm_IsAcceptedWithoutParsingParameters()
    {
        Succeeded(CotpFrames.ReadConnectionConfirm(ToBytes(V_S7_2)));
    }

    [Fact]
    public void V_S7_2_OtherTsapInTheConfirm_StillAccepted()
    {
        // 计划第 2 条：CC 只校验 PDU 类型 0xD0，参数不解析（最后一个字节属于 TSAP 参数）。
        Succeeded(CotpFrames.ReadConnectionConfirm(Mutated(V_S7_2, ConfirmParameterTail, 0x02)));
    }

    [Fact]
    public void Cc_WrongPduType_IsNotAConnectionConfirm()
    {
        AssertViolation(CotpFrames.ReadConnectionConfirm(ToBytes(Cc_WrongPduType)));
    }

    [Theory]
    [InlineData(V_S7_4, 240)]
    [InlineData(Setup_Ack_960, 960)]
    public void V_S7_4_SetupAck_ReturnsTheNegotiatedPduSize(string frame, int expected)
    {
        int negotiated = Succeeded(S7SetupCommunication.ReadNegotiatedPdu(ToBytes(frame), SetupReference));

        Assert.Equal(expected, negotiated);
    }

    [Fact]
    public void V_S7_6_ReadAck_ReturnsTheFourDataBytes()
    {
        byte[] data = Succeeded(S7ReadVar.ReadResponse(ToBytes(V_S7_6), ReadDbReference, 4));

        Assert.Equal("41 20 00 00", ToText(data));
    }

    [Fact]
    public void V_S7_10_ReadAck_ReturnsTheSingleDataByte()
    {
        byte[] data = Succeeded(S7ReadVar.ReadResponse(ToBytes(V_S7_10_Ack), ReadMerkerReference, 1));

        Assert.Equal("5A", ToText(data));
    }

    [Theory]
    [InlineData(V_S7_6, ReadDbReference, 4)]
    [InlineData(Read_Ack_OctetString, ReadDbReference, 4)]
    [InlineData(Read_Ack_32Bits, ReadDbReference, 4)]
    [InlineData(Read_Ack_3Bits, ReadMerkerReference, 1)]
    public void ReadAck_AcceptsEveryDocumentedTransportSize(string frame, ushort reference, int bytes)
    {
        // 17.3 节：0x04 与 0x03 的长度按位计，0x09 按字节计。
        byte[] data = Succeeded(S7ReadVar.ReadResponse(ToBytes(frame), reference, bytes));

        Assert.Equal(bytes, data.Length);
    }

    [Fact]
    public void V_S7_8_WriteAck_IsSuccess()
    {
        Succeeded(S7WriteVar.ReadResponse(ToBytes(V_S7_8), WriteReference));
    }

    [Fact]
    public void V_S7_8_AckWithRosctr2_IsAlsoAccepted()
    {
        Succeeded(S7WriteVar.ReadResponse(Mutated(V_S7_8, Rosctr, 0x02), WriteReference));
    }

    [Fact]
    public void V_S7_11_ItemReturnCode0A_IsRemoteErrorWithPutGetHint()
    {
        AssertRemoteError<byte[]>(
            S7ReadVar.ReadResponse(ToBytes(V_S7_11), ItemErrorReference, 4),
            0x0A,
            "S7 item return code 0x0A (object does not exist). " + PutGetHint);
    }

    [Theory]
    [InlineData(0x01, "S7 item return code 0x01 (hardware fault).")]
    [InlineData(0x03, "S7 item return code 0x03 (accessing the object not allowed). " + PutGetHint)]
    [InlineData(0x05, "S7 item return code 0x05 (invalid address). " + PutGetHint)]
    [InlineData(0x06, "S7 item return code 0x06 (data type not supported).")]
    [InlineData(0x07, "S7 item return code 0x07 (data type inconsistent).")]
    [InlineData(0x0A, "S7 item return code 0x0A (object does not exist). " + PutGetHint)]
    [InlineData(0x02, "S7 item return code 0x02.")]
    public void ItemReturnCodes_UseOnlyTheNamesDocumentedInTheCodeTable(int returnCode, string message)
    {
        byte[] frame = Mutated(V_S7_11, ItemReturnCode, (byte)returnCode);

        AssertRemoteError<byte[]>(S7ReadVar.ReadResponse(frame, ItemErrorReference, 4), returnCode, message);
    }

    [Fact]
    public void V_S7_12_HeaderError_IsRemoteErrorWithPutGetHint()
    {
        AssertRemoteError<byte[]>(
            S7ReadVar.ReadResponse(ToBytes(V_S7_12), HeaderErrorReference, 4),
            0x8104,
            "S7 error class 0x81 code 0x04. " + PutGetHint);
    }

    [Fact]
    public void V_S7_12_HeaderError_IsAlsoReportedForWrites()
    {
        AssertRemoteError(
            S7WriteVar.ReadResponse(ToBytes(V_S7_12), HeaderErrorReference),
            0x8104,
            "S7 error class 0x81 code 0x04. " + PutGetHint);
    }

    [Fact]
    public void V_S7_12_HeaderError_IsAlsoReportedForSetup()
    {
        AssertRemoteError<int>(
            S7SetupCommunication.ReadNegotiatedPdu(ToBytes(V_S7_12), HeaderErrorReference),
            0x8104,
            "S7 error class 0x81 code 0x04. " + PutGetHint);
    }

    [Theory]
    [InlineData(0x00, 0x07, 0x07)]
    [InlineData(0x82, 0x00, 0x8200)]
    [InlineData(0x87, 0x03, 0x8703)]
    public void HeaderErrors_CombineClassAndCodeAsTheProtocolErrorCode(int errorClass, int errorCode, long expectedCode)
    {
        byte[] frame = Mutated(V_S7_12, ErrorClass, (byte)errorClass);
        frame[ErrorCode] = (byte)errorCode;

        AssertRemoteError(
            S7WriteVar.ReadResponse(frame, HeaderErrorReference),
            expectedCode,
            $"S7 error class 0x{errorClass:X2} code 0x{errorCode:X2}.");
    }

    [Fact]
    public void Write_Ack_WithPadding_IsSuccess()
    {
        // 写应答只有返回码：数据长度字段多出的填充字节不参与判断（17.1 节说明段只保证读应答不补填充）。
        Succeeded(S7WriteVar.ReadResponse(ToBytes(Write_Ack_Padded), WriteReference));
    }
}
