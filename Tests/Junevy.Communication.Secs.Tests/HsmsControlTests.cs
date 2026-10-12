using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 控制消息的构造与 HSMS 分帧声明（计划 Task 4、P51-D6、D8）：
/// 每个工厂对上自己的黄金向量；分帧按 4 字节长度切帧且把长度前缀留给协议层。
/// </summary>
public sealed class HsmsControlTests
{
    // 逐字节的向量比对在 HsmsMessageTests（那里 18 条向量各一次）；这里只看每个工厂造出来的头字段，
    // 两份用例因此互补而不是重复。
    [Fact]
    public void SelectAndLinktestFactories_PutTheStatusAndTheKindWhereTheSpecSays()
    {
        AssertHeader(HsmsControl.SelectRequest(1), HsmsControlKind.SelectRequest, 0, 0, HsmsMessage.AnySession, 1);
        AssertHeader(HsmsControl.SelectResponse(1, HsmsControl.SelectOk), HsmsControlKind.SelectResponse, 0, 0, HsmsMessage.AnySession, 1);
        AssertHeader(HsmsControl.SelectResponse(1, HsmsControl.SelectAlreadyActive), HsmsControlKind.SelectResponse, 0, 1, HsmsMessage.AnySession, 1);
        AssertHeader(HsmsControl.LinktestRequest(7), HsmsControlKind.LinktestRequest, 0, 0, HsmsMessage.AnySession, 7);
        AssertHeader(HsmsControl.LinktestResponse(7), HsmsControlKind.LinktestResponse, 0, 0, HsmsMessage.AnySession, 7);
        AssertHeader(HsmsControl.SeparateRequest(8), HsmsControlKind.SeparateRequest, 0, 0, HsmsMessage.AnySession, 8);
        AssertHeader(HsmsControl.DeselectRequest(3), HsmsControlKind.DeselectRequest, 0, 0, HsmsMessage.AnySession, 3);
        AssertHeader(HsmsControl.DeselectResponse(4), HsmsControlKind.DeselectResponse, 0, 0, HsmsMessage.AnySession, 4);
    }

    [Fact]
    public void RejectPutsTheOffendingTypeAndTheReasonInTheSecondAndThirdHeaderBytes()
    {
        // Reject 是唯一回送会话号而不是 0xFFFF 的控制消息：被拒的是数据消息时会话号是设备号。
        AssertHeader(HsmsControl.Reject(1, 0, HsmsRejectReason.NotSelected, 2), HsmsControlKind.RejectRequest, 0, (byte)HsmsRejectReason.NotSelected, 1, 2);
        AssertHeader(
            HsmsControl.Reject(HsmsMessage.AnySession, (byte)HsmsControlKind.DeselectRequest, HsmsRejectReason.NotUnderstood, 9),
            HsmsControlKind.RejectRequest,
            (byte)HsmsControlKind.DeselectRequest,
            (byte)HsmsRejectReason.NotUnderstood,
            HsmsMessage.AnySession,
            9);
        AssertHeader(
            HsmsControl.Reject(HsmsMessage.AnySession, 5, HsmsRejectReason.PTypeNotSupported, 10),
            HsmsControlKind.RejectRequest,
            5,
            (byte)HsmsRejectReason.PTypeNotSupported,
            HsmsMessage.AnySession,
            10);
    }

    [Fact]
    public void ControlMessagesCarryNoBody()
    {
        Assert.Equal(0, HsmsControl.SelectRequest(1).Body.Length);
        Assert.Equal(0, HsmsControl.Reject(1, 0, HsmsRejectReason.NotSelected, 2).Body.Length);
    }

    [Fact]
    public void Framing_SplitsOnTheLengthFieldAndKeepsItForTheProtocolLayer()
    {
        IFrameDecoder decoder = HsmsFraming.Create(MaxFrameLength).CreateDecoder();
        var buffer = new ReadOnlySequence<byte>(Concat(
            HexVector.ToBytes("00 00 00 0A FF FF 00 00 00 01 00 00 00 01"),
            HexVector.ToBytes("00 00 00 19 00 01 01 02 00 00 00 00 00 03 01 02 41 04 45 51 30 31 41 05 31 2E 30 2E 30")));

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> first));
        Assert.Equal("00 00 00 0A FF FF 00 00 00 01 00 00 00 01", HexVector.ToText(first.ToArray()));

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> second));
        Assert.Equal("00 00 00 19 00 01 01 02 00 00 00 00 00 03 01 02 41 04 45 51 30 31 41 05 31 2E 30 2E 30", HexVector.ToText(second.ToArray()));

        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Framing_WaitsForTheRestOfAnIncompleteFrame()
    {
        IFrameDecoder decoder = HsmsFraming.Create(MaxFrameLength).CreateDecoder();
        var buffer = new ReadOnlySequence<byte>(HexVector.Truncated("00 00 00 19 00 01 01 02 00 00 00 00 00 03 01 02 41 04 45 51 30 31 41 05 31 2E 30 2E 30", 12));

        Assert.False(decoder.TryDecode(ref buffer, out _));
        Assert.True(buffer.Length > 0);
    }

    [Fact]
    public void Framing_RejectsAFrameLongerThanTheLimit()
    {
        IFrameDecoder decoder = HsmsFraming.Create(8).CreateDecoder();
        var buffer = new ReadOnlySequence<byte>(HexVector.ToBytes("00 00 00 1F 00 01 85 01 00 00 00 00 01 01 01 03 21 01 80 B1 04 00 00 03 E9 41 08 4F 56 45 52 48 45 41 54"));

        Assert.Throws<FrameDecodeException>(() => decoder.TryDecode(ref buffer, out _));
    }

    private static void AssertHeader(
        HsmsMessage message, HsmsControlKind kind, byte byte2, byte byte3, ushort sessionId, uint systemBytes)
    {
        Assert.Equal(kind, message.Kind);
        Assert.Equal(byte2, message.Byte2);
        Assert.Equal(byte3, message.Byte3);
        Assert.Equal(sessionId, message.SessionId);
        Assert.Equal(systemBytes, message.SystemBytes);
        Assert.Equal(0, message.PType);
        Assert.Equal((byte)HsmsMessage.HeaderBytes, message.ToArray().Length - HsmsMessage.LengthPrefixBytes);
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var joined = new byte[first.Length + second.Length];
        first.CopyTo(joined, 0);
        second.CopyTo(joined, first.Length);
        return joined;
    }

    private const int MaxFrameLength = 1024 * 1024;
}