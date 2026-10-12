using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// HSMS 报文的编码与识别（计划 Task 4、P51-D6）：第 17.1 节全部黄金向量逐字节比较，
/// 以及长度字段不符、头不足 10 字节、PType 非 0 三种识别。
/// </summary>
public sealed class HsmsMessageTests
{
    private const ushort DeviceId = 1;

    [Theory]
    // V-H-1 … V-H-9：控制消息。
    [InlineData("00 00 00 0A FF FF 00 00 00 01 00 00 00 01")]
    [InlineData("00 00 00 0A FF FF 00 00 00 02 00 00 00 01")]
    [InlineData("00 00 00 0A FF FF 00 01 00 02 00 00 00 01")]
    [InlineData("00 00 00 0A FF FF 00 00 00 05 00 00 00 07")]
    [InlineData("00 00 00 0A FF FF 00 00 00 06 00 00 00 07")]
    [InlineData("00 00 00 0A FF FF 00 00 00 09 00 00 00 08")]
    [InlineData("00 00 00 0A 00 01 00 04 00 07 00 00 00 02")]
    [InlineData("00 00 00 0A FF FF 03 01 00 07 00 00 00 09")]
    [InlineData("00 00 00 0A FF FF 05 02 00 07 00 00 00 0A")]
    // V-H-10 … V-H-18：数据消息与 S9 消息。
    [InlineData("00 00 00 0A 00 01 81 01 00 00 00 00 00 03")]
    [InlineData("00 00 00 19 00 01 01 02 00 00 00 00 00 03 01 02 41 04 45 51 30 31 41 05 31 2E 30 2E 30")]
    [InlineData("00 00 00 1F 00 01 85 01 00 00 00 00 01 01 01 03 21 01 80 B1 04 00 00 03 E9 41 08 4F 56 45 52 48 45 41 54")]
    [InlineData("00 00 00 0D 00 01 05 02 00 00 00 00 01 01 21 01 00")]
    [InlineData("00 00 00 16 00 01 09 05 00 00 00 00 02 02 21 0A 00 01 81 63 00 00 00 00 00 04")]
    [InlineData("00 00 00 0A 00 01 02 00 00 00 00 00 00 05")]
    [InlineData("00 00 00 16 00 01 09 09 00 00 00 00 03 04 21 0A 00 01 86 0B 00 00 00 00 03 03")]
    [InlineData("00 00 00 16 00 01 09 01 00 00 00 00 02 03 21 0A 00 02 81 01 00 00 00 00 00 06")]
    [InlineData("00 00 00 16 00 01 09 07 00 00 00 00 02 04 21 0A 00 01 82 29 00 00 00 00 00 07")]
    public void EveryVector_SurvivesDecodeAndEncodeUnchanged(string hex)
    {
        HsmsMessage decoded = Read(hex);

        Assert.Equal(hex, HexVector.ToText(Encode(decoded)));
    }

    [Fact]
    public void V_H_1_SelectRequest()
    {
        HsmsMessage message = Read("00 00 00 0A FF FF 00 00 00 01 00 00 00 01");

        Assert.Equal(HsmsControlKind.SelectRequest, message.Kind);
        Assert.Equal((ushort)0xFFFF, message.SessionId);
        Assert.Equal(1u, message.SystemBytes);
        Assert.Equal(0, message.Body.Length);
    }

    [Fact]
    public void V_H_2_SelectResponseCarriesTheStatusInTheSecondHeaderByte()
    {
        HsmsMessage message = Read("00 00 00 0A FF FF 00 00 00 02 00 00 00 01");

        Assert.Equal(HsmsControlKind.SelectResponse, message.Kind);
        Assert.Equal(0, message.Status);
    }

    [Fact]
    public void V_H_3_SelectResponseStatusOne()
    {
        HsmsMessage message = Read("00 00 00 0A FF FF 00 01 00 02 00 00 00 01");

        Assert.Equal(HsmsControlKind.SelectResponse, message.Kind);
        Assert.Equal(1, message.Status);
    }

    [Fact]
    public void V_H_4_And_V_H_5_LinktestPair()
    {
        HsmsMessage request = Read("00 00 00 0A FF FF 00 00 00 05 00 00 00 07");
        HsmsMessage response = Read("00 00 00 0A FF FF 00 00 00 06 00 00 00 07");

        Assert.Equal(HsmsControlKind.LinktestRequest, request.Kind);
        Assert.Equal(HsmsControlKind.LinktestResponse, response.Kind);
        Assert.Equal(request.SystemBytes, response.SystemBytes);
    }

    [Fact]
    public void V_H_6_SeparateRequest()
    {
        Assert.Equal(HsmsControlKind.SeparateRequest, Read("00 00 00 0A FF FF 00 00 00 09 00 00 00 08").Kind);
    }

    [Fact]
    public void V_H_7_RejectNotSelectedEchoesTheSessionOfTheRejectedMessage()
    {
        HsmsMessage message = Read("00 00 00 0A 00 01 00 04 00 07 00 00 00 02");

        Assert.Equal(HsmsControlKind.RejectRequest, message.Kind);
        Assert.Equal(DeviceId, message.SessionId);
        Assert.Equal((byte)HsmsRejectReason.NotSelected, message.RejectReason);
    }

    [Fact]
    public void V_H_8_And_V_H_9_RejectCarryTheOffendingTypeAndTheReason()
    {
        HsmsMessage notUnderstood = Read("00 00 00 0A FF FF 03 01 00 07 00 00 00 09");
        HsmsMessage wrongPType = Read("00 00 00 0A FF FF 05 02 00 07 00 00 00 0A");

        Assert.Equal((byte)HsmsControlKind.DeselectRequest, notUnderstood.OffendingType);
        Assert.Equal((byte)HsmsRejectReason.NotUnderstood, notUnderstood.RejectReason);
        Assert.Equal((byte)5, wrongPType.OffendingType);
        Assert.Equal((byte)HsmsRejectReason.PTypeNotSupported, wrongPType.RejectReason);
    }

    [Fact]
    public void V_H_10_PrimaryMessageKeepsItsWBitStreamAndFunction()
    {
        HsmsMessage message = Read("00 00 00 0A 00 01 81 01 00 00 00 00 00 03");

        Assert.Equal(HsmsControlKind.None, message.Kind);
        Assert.Equal(DeviceId, message.SessionId);
        Assert.Equal((byte)1, message.Stream);
        Assert.True(message.ReplyExpected);
        Assert.Equal((byte)1, message.Function);
        Assert.Equal(0, message.Body.Length);
    }

    [Fact]
    public void V_H_11_ReplyMessageDropsTheWBitAndIncrementsTheFunction()
    {
        HsmsMessage message = Read("00 00 00 19 00 01 01 02 00 00 00 00 00 03 01 02 41 04 45 51 30 31 41 05 31 2E 30 2E 30");

        Assert.Equal((byte)1, message.Stream);
        Assert.False(message.ReplyExpected);
        Assert.Equal((byte)2, message.Function);
        Assert.Equal("<L [2]\n  <A \"EQ01\">\n  <A \"1.0.0\">\n>", Items(message).ToSml());
    }

    [Fact]
    public void V_H_12_And_V_H_13_TheReportAndItsReply()
    {
        HsmsMessage report = Read("00 00 00 1F 00 01 85 01 00 00 00 00 01 01 01 03 21 01 80 B1 04 00 00 03 E9 41 08 4F 56 45 52 48 45 41 54");
        HsmsMessage reply = Read("00 00 00 0D 00 01 05 02 00 00 00 00 01 01 21 01 00");

        Assert.Equal((byte)5, report.Stream);
        Assert.True(report.ReplyExpected);
        Assert.Equal(0x101u, report.SystemBytes);
        SecsItem reportBody = Items(report);
        Assert.Equal(
            "S5F1 W\n<L [3]\n  <B 0x80>\n  <U4 1001>\n  <A \"OVERHEAT\">\n>\n.",
            SecsMessage.Create(5, 1, replyExpected: true, body: reportBody).ToSml());
        Assert.Equal(1001u, Assert.Single(reportBody.Items[1].GetU4()));

        Assert.False(reply.ReplyExpected);
        Assert.Equal((byte)2, reply.Function);
        Assert.Equal(new byte[] { 0x00 }, Items(reply).GetBinary());
    }

    [Fact]
    public void S9MessagesCarryTheTenByteHeaderOfTheOffendingMessageAsABinaryItem()
    {
        HsmsMessage s9f5 = Read("00 00 00 16 00 01 09 05 00 00 00 00 02 02 21 0A 00 01 81 63 00 00 00 00 00 04");

        Assert.Equal((byte)9, s9f5.Stream);
        Assert.Equal((byte)5, s9f5.Function);
        Assert.Equal(
            new byte[] { 0x00, 0x01, 0x81, 0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04 },
            Items(s9f5).GetBinary());
    }

    [Fact]
    public void DataMessagesAreBuiltFromTheSecsMessageWithAnExplicitSystemBytes()
    {
        SecsMessage primary = SecsMessage.Create(1, 1, replyExpected: true, body: SecsItem.U4(1));

        byte[] frame = HsmsMessage.Write(primary, DeviceId, 3u);

        Assert.Equal("00 00 00 10 00 01 81 01 00 00 00 00 00 03 B1 04 00 00 00 01", HexVector.ToText(frame));
    }

    [Fact]
    public void DecodingRejectsALengthFieldThatDoesNotMatchTheFrame()
    {
        AssertFails("00 00 00 0B FF FF 00 00 00 01 00 00 00 01", "declares 11 byte(s) but the frame carries 10");
        AssertFails("00 00 00 09 FF FF 00 00 00 01 00 00 00 01", "declares 9 byte(s) but the frame carries 10");
    }

    [Theory]
    [InlineData("00 00 00 00")]
    [InlineData("00 00 00 03 00 01 02")]
    [InlineData("00 00 00 09 FF FF 00 00 00 01 02 03 04")]
    public void DecodingRejectsAHeaderShorterThanTenBytes(string hex)
    {
        AssertFails(hex, "10 byte header");
    }

    [Fact]
    public void DecodingRejectsAFrameWithoutEvenALengthPrefix()
    {
        AssertFails("", "length byte(s) are missing");
        AssertFails("00 00 00", "length byte(s) are missing");
    }

    [Fact]
    public void DecodingRejectsAPTypeOtherThanZero()
    {
        AssertFails("00 00 00 0A FF FF 00 00 05 01 00 00 00 01", "PType 5");
    }

    [Fact]
    public void AnUnknownControlTypeIsRecognisedAsUnknownRatherThanData()
    {
        HsmsMessage message = Read("00 00 00 0A FF FF 00 00 00 63 00 00 00 01");

        // 0x63 不是已分配的 SType：Kind 原样带出来，派发方据此回 Reject.req 原因 1。
        Assert.NotEqual(HsmsControlKind.None, message.Kind);
        Assert.Equal((HsmsControlKind)0x63, message.Kind);
        Assert.Equal((byte)0x63, message.SType);
    }

    private static SecsItem Items(HsmsMessage message)
    {
        CommResult<SecsItem> decoded = SecsItemCodec.Decode(message.Body, GenerousLimit);
        Assert.True(decoded.IsSuccess, decoded.ErrorMessage);
        return decoded.Data;
    }

    private static HsmsMessage Read(string hex)
    {
        CommResult<HsmsMessage> result = HsmsMessage.Read(HexVector.ToBytes(hex), GenerousLimit);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Data;
    }

    private static void AssertFails(string hex, string expectedFragment)
    {
        CommResult<HsmsMessage> result = HsmsMessage.Read(HexVector.ToBytes(hex), GenerousLimit);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Contains(expectedFragment, result.ErrorMessage);
    }

    private static byte[] Encode(HsmsMessage message) => message.ToArray();

    private const int GenerousLimit = 1024 * 1024;
}