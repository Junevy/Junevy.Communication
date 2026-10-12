using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec.Protocol;
using static Junevy.Communication.Melsec.Tests.McTestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 串口 4C 帧（计划 Task 10）：第 18 节 V7–V10 逐字节核对（V8、V9 双向），附加码在编号、点数与数据中各出现一次，
/// 和校验关闭的帧，以及帧识别号、和校验、响应 ID 与结束码的错误分类。
/// 控制字节（ENQ、STX、ETX、ACK、NAK、DLE）用 <see cref="Hex"/> 写出，帧识别号、访问路径、数据与和校验是 ASCII 字符。
/// </summary>
public sealed class Mc4CFrameTests
{
    [Fact(Timeout = 20000)]
    public void V7_Binary4C_Format5_BitRead_EncodesTheRequestWithChecksum()
    {
        var route = new Mc4CAccessRoute(0x05, 0x07, 0x03, 0x0004, 0x01, 0x00);

        byte[] request = Encode4C(McEncoding.Binary, true, route, BitRead(McEncoding.Binary, "X40", 5));

        Assert.Equal(HexVector.ToBytes("10 02 12 00 F8 05 07 03 04 00 01 00 01 04 01 00 40 00 00 9C 05 00 10 03 30 35"), request);
    }

    [Fact(Timeout = 20000)]
    public void V8_Binary4C_Format5_WordRead_EscapesTheNumberAndDecodesTheResponse()
    {
        // 编号 0010H 触发附加码；应答的数据 0010H、1010H 也含 10H。
        byte[] request = Encode4C(McEncoding.Binary, true, DefaultSerialRoute, WordRead(McEncoding.Binary, "M16", 2));
        Assert.Equal(HexVector.ToBytes("10 02 12 00 F8 00 00 FF FF 03 00 00 01 04 00 00 10 10 00 00 90 02 00 10 03 42 32"), request);

        McResponse response = Succeeded(Decode4C(McEncoding.Binary, true,
            HexVector.ToBytes("10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03 33 37"), McDataKind.Words, 2));
        Assert.Equal((ushort)0, response.EndCode);
        Assert.Equal(HexVector.ToBytes("10 00 10 10"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void V9_Ascii4C_Format4_WordRead_EncodesTheRequestAndDecodesTheResponse()
    {
        // CR LF 由通道分隔符处理，不在帧内。
        byte[] request = Encode4C(McEncoding.Ascii, true, DefaultSerialRoute, WordRead(McEncoding.Ascii, "TN100", 3));
        Assert.Equal(Join(HexVector.ToBytes("05"), Text("F80000FF03FF0000" + "04010000TN0001000003"), Text("84")), request);

        McResponse response = Succeeded(Decode4C(McEncoding.Ascii, true,
            Join(HexVector.ToBytes("02"), Text("F80000FF03FF0000" + "123400021DEF"), HexVector.ToBytes("03"), Text("08")), McDataKind.Words, 3));
        Assert.Equal(HexVector.ToBytes("34 12 02 00 EF 1D"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void V10_Ascii4C_Format4_Nak_IsARemoteErrorWithTheErrorCode()
    {
        CommResult check = Succeeded(Decode4C(McEncoding.Ascii, true, Join(HexVector.ToBytes("15"), Text("F80000FF03FF0000" + "C051")))).CheckEndCode();

        Assert.Equal(CommErrorKind.RemoteError, check.ErrorKind);
        Assert.Equal(0xC051L, check.ProtocolErrorCode);
    }

    [Fact(Timeout = 20000)]
    public void Request_Format5_EscapesTheDleInThePointCountAndTheData_OncePerField()
    {
        // 点数 16（0010H）含一个 10H；和校验关闭，便于逐字节核对附加码的位置。
        byte[] points = Encode4C(McEncoding.Binary, false, DefaultSerialRoute, BitRead(McEncoding.Binary, "X0", 16));
        Assert.Equal(HexVector.ToBytes("10 02 12 00 F8 00 00 FF FF 03 00 00 01 04 01 00 00 00 00 9C 10 10 00 10 03"), points);

        // 写入的字 0010H 含一个 10H。
        byte[] data = Encode4C(McEncoding.Binary, false, DefaultSerialRoute, WordWrite(McEncoding.Binary, "D100", HexVector.ToBytes("10 00")));
        Assert.Equal(HexVector.ToBytes("10 02 14 00 F8 00 00 FF FF 03 00 00 01 14 00 00 64 00 00 A8 01 00 10 10 00 10 03"), data);
    }

    [Fact(Timeout = 20000)]
    public void Request_Format4_WithChecksumOff_HasNoChecksum()
    {
        byte[] request = Encode4C(McEncoding.Ascii, false, DefaultSerialRoute, WordRead(McEncoding.Ascii, "TN100", 3));

        Assert.Equal(Join(HexVector.ToBytes("05"), Text("F80000FF03FF0000" + "04010000TN0001000003")), request);
    }

    [Fact(Timeout = 20000)]
    public void Response_Format5_WithChecksumOff_DecodesWithoutAChecksum()
    {
        McResponse response = Succeeded(Decode4C(McEncoding.Binary, false,
            HexVector.ToBytes("10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03"), McDataKind.Words, 2));

        Assert.Equal(HexVector.ToBytes("10 00 10 10"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void Response_Format5_ErrorCompletion_IsARemoteErrorWithTheErrorCode()
    {
        // 完成码的位置是错误码（小端，P2-D16 ②）；数据字节数 0CH = 帧识别号 + 访问路径 + 响应 ID + 错误码。
        McResponse response = Succeeded(Decode4C(McEncoding.Binary, false,
            HexVector.ToBytes("10 02 0C 00 F8 00 00 FF FF 03 00 00 FF FF 51 C0 10 03"), McDataKind.Words, 2));

        CommResult check = response.CheckEndCode();
        Assert.Equal(CommErrorKind.RemoteError, check.ErrorKind);
        Assert.Equal(0xC051L, check.ProtocolErrorCode);
    }

    [Fact(Timeout = 20000)]
    public void Response_Format4_Ack_IsSuccessWithoutData_ForAWrite()
    {
        McResponse response = Succeeded(Decode4C(McEncoding.Ascii, true, Join(HexVector.ToBytes("06"), Text("F80000FF03FF0000"))));

        Assert.Equal((ushort)0, response.EndCode);
        Assert.Empty(response.Data);
    }

    [Fact(Timeout = 20000)]
    public void Response_Format4_Ack_ForARead_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Ascii, true, Join(HexVector.ToBytes("06"), Text("F80000FF03FF0000")), McDataKind.Words, 3));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format4_WithChecksumOff_DecodesWithoutAChecksum()
    {
        McResponse response = Succeeded(Decode4C(McEncoding.Ascii, false,
            Join(HexVector.ToBytes("02"), Text("F80000FF03FF0000" + "123400021DEF"), HexVector.ToBytes("03")), McDataKind.Words, 3));

        Assert.Equal(HexVector.ToBytes("34 12 02 00 EF 1D"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void Response_Format4_WrongChecksum_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Ascii, true,
            Join(HexVector.ToBytes("02"), Text("F80000FF03FF0000" + "123400021DEF"), HexVector.ToBytes("03"), Text("09")), McDataKind.Words, 3));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format4_WrongFrameId_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Ascii, false,
            Join(HexVector.ToBytes("02"), Text("F90000FF03FF0000" + "123400021DEF"), HexVector.ToBytes("03")), McDataKind.Words, 3));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format4_WithoutEtx_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Ascii, false,
            Join(HexVector.ToBytes("02"), Text("F80000FF03FF0000" + "123400021DEF")), McDataKind.Words, 3));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format4_UnknownControlCode_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Ascii, false, Join(HexVector.ToBytes("7F"), Text("F80000FF03FF0000"))));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format5_WrongChecksum_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Binary, true,
            HexVector.ToBytes("10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03 33 38"), McDataKind.Words, 2));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format5_WrongFrameId_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Binary, false,
            HexVector.ToBytes("10 02 10 10 00 F9 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03"), McDataKind.Words, 2));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format5_WrongResponseId_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Binary, false,
            HexVector.ToBytes("10 02 10 10 00 F8 00 00 FF FF 03 00 00 00 00 00 00 10 10 00 10 10 10 10 10 03"), McDataKind.Words, 2));
    }

    [Fact(Timeout = 20000)]
    public void Response_Format5_ByteCountMismatch_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode4C(McEncoding.Binary, false,
            HexVector.ToBytes("10 02 11 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03"), McDataKind.Words, 2));
    }
}
