using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec.Protocol;
using static Junevy.Communication.Melsec.Tests.McTestSupport;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 3E / 4E 以太网帧（计划 Task 9）：第 18 节 V1–V6 逐字节核对（请求编码与应答解码双向），
/// 另含手册 p.185 的 ASCII 回环示例，以及副头部、长度、序列号、数据长度与结束码的错误分类。
/// </summary>
public sealed class McEthernetFrameTests
{
    [Fact(Timeout = 20000)]
    public void V1_Binary3E_WordRead_EncodesTheRequestAndDecodesTheWords()
    {
        byte[] request = Encode(McFrameType.Frame3E, McEncoding.Binary, 0, WordRead(McEncoding.Binary, "TN100", 3));
        Assert.Equal(Hex("50 00 00 FF FF 03 00 0C 00 10 00 01 04 00 00 64 00 00 C2 03 00"), request);

        McResponse response = Succeeded(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D0 00 00 FF FF 03 00 08 00 00 00 34 12 02 00 EF 1D"), McDataKind.Words, 3));
        Assert.Equal((ushort)0, response.EndCode);
        Assert.Equal(Hex("34 12 02 00 EF 1D"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void V2_Ascii3E_WordRead_EncodesTheRequestAndDecodesTheWords()
    {
        byte[] request = Encode(McFrameType.Frame3E, McEncoding.Ascii, 0, WordRead(McEncoding.Ascii, "TN100", 3));
        Assert.Equal(Text("5000" + "00FF03FF00" + "0018" + "0010" + "04010000TN0001000003"), request);

        McResponse response = Succeeded(Decode(McFrameType.Frame3E, McEncoding.Ascii, 0,
            Text("D000" + "00FF03FF00" + "0010" + "0000" + "123400021DEF"), McDataKind.Words, 3));
        Assert.Equal(Hex("34 12 02 00 EF 1D"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void V3_Binary4E_BitRead_EncodesTheSerialAndDecodesTheEightPoints()
    {
        byte[] request = Encode(McFrameType.Frame4E, McEncoding.Binary, 0x1234, BitRead(McEncoding.Binary, "M100", 8));
        Assert.Equal(Hex("54 00 34 12 00 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 90 08 00"), request);

        McResponse response = Succeeded(Decode(McFrameType.Frame4E, McEncoding.Binary, 0x1234,
            Hex("D4 00 34 12 00 00 00 FF FF 03 00 06 00 00 00 00 01 00 11"), McDataKind.Bits, 8));
        Assert.Equal(new[] { false, false, false, true, false, false, true, true }, response.Bits);
    }

    [Fact(Timeout = 20000)]
    public void V4_Ascii4E_WordWrite_EncodesTheSerialAndDecodesTheEndCode()
    {
        // 1995H、1202H、1130H 的规范化布局（字按地址递增，每字小端）。
        byte[] request = Encode(McFrameType.Frame4E, McEncoding.Ascii, 0x1234,
            WordWrite(McEncoding.Ascii, "D100", Hex("95 19 02 12 30 11")));
        Assert.Equal(Text("5400" + "1234" + "0000" + "00FF03FF00" + "0024" + "0010" + "14010000D*0001000003199512021130"), request);

        McResponse response = Succeeded(Decode(McFrameType.Frame4E, McEncoding.Ascii, 0x1234,
            Text("D400" + "1234" + "0000" + "00FF03FF00" + "0004" + "0000")));
        Assert.Equal((ushort)0, response.EndCode);
        Assert.Empty(response.Data);
    }

    [Fact(Timeout = 20000)]
    public void V5_Binary3E_ErrorEndCode_IsARemoteErrorCarryingTheEndCode()
    {
        McResponse response = Succeeded(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D0 00 00 FF FF 03 00 0B 00 51 C0 00 FF FF 03 00 01 04 00 00"), McDataKind.Words, 3));

        CommResult check = response.CheckEndCode();
        Assert.Equal(CommErrorKind.RemoteError, check.ErrorKind);
        Assert.Equal(0xC051L, check.ProtocolErrorCode);
        Assert.Equal("MC end code 0xC051.", check.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public void V6_Binary3E_Loopback_EncodesTheRequestAndDecodesTheEcho()
    {
        byte[] request = Encode(McFrameType.Frame3E, McEncoding.Binary, 0, Loopback(McEncoding.Binary, Text("ABCDE")));
        Assert.Equal(Hex("50 00 00 FF FF 03 00 0D 00 10 00 19 06 00 00 05 00 41 42 43 44 45"), request);

        McResponse response = Succeeded(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D0 00 00 FF FF 03 00 09 00 00 00 05 00 41 42 43 44 45"), McDataKind.Loopback, 5));
        Assert.Equal(Text("ABCDE"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void Loopback_Ascii_CarriesTheCharactersUnchanged_AsInManualPage185()
    {
        // 数据段取手册 p.185 的 ASCII 示例；3E 头部按 V2 的规则（长度 = 监视定时器 4 + 数据 17 = 0x15）。
        byte[] request = Encode(McFrameType.Frame3E, McEncoding.Ascii, 0, Loopback(McEncoding.Ascii, Text("ABCDE")));
        Assert.Equal(Text("5000" + "00FF03FF00" + "0015" + "0010" + "0619" + "0000" + "0005" + "ABCDE"), request);

        McResponse response = Succeeded(Decode(McFrameType.Frame3E, McEncoding.Ascii, 0,
            Text("D000" + "00FF03FF00" + "000D" + "0000" + "0005" + "ABCDE"), McDataKind.Loopback, 5));
        Assert.Equal(Text("ABCDE"), response.Data);
    }

    [Fact(Timeout = 20000)]
    public void Response_WithAWrongSubheader_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D1 00 00 FF FF 03 00 08 00 00 00 34 12 02 00 EF 1D"), McDataKind.Words, 3));
    }

    [Fact(Timeout = 20000)]
    public void Response_WhoseLengthFieldDisagreesWithTheFrame_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D0 00 00 FF FF 03 00 07 00 00 00 34 12 02 00 EF 1D"), McDataKind.Words, 3));
    }

    [Fact(Timeout = 20000)]
    public void Response_4EWithAnotherSerial_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame4E, McEncoding.Binary, 0x1234,
            Hex("D4 00 35 12 00 00 00 FF FF 03 00 06 00 00 00 00 01 00 11"), McDataKind.Bits, 8));
    }

    [Fact(Timeout = 20000)]
    public void Response_WithFewerWordsThanRequested_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D0 00 00 FF FF 03 00 08 00 00 00 34 12 02 00 EF 1D"), McDataKind.Words, 2));
    }

    [Fact(Timeout = 20000)]
    public void Response_TooShortForAnEndCode_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D0 00 00 FF FF 03 00 01 00 00"), McDataKind.Words, 1));
    }

    [Fact(Timeout = 20000)]
    public void Response_WithAnInvalidAsciiDigit_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame3E, McEncoding.Ascii, 0,
            Text("D000" + "00FF03FF00" + "0010" + "0000" + "123400021DEG"), McDataKind.Words, 3));
    }

    [Fact(Timeout = 20000)]
    public void Response_ToAWriteThatCarriesData_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame4E, McEncoding.Ascii, 0x1234,
            Text("D400" + "1234" + "0000" + "00FF03FF00" + "0006" + "0000" + "12")));
    }
}
