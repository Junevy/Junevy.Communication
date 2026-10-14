using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 设备端发出主消息（计划 Task 6，P51-D14）：带 W 位按 T3 等应答（S5F2 / <c>S5F0</c> / <c>Reject.req</c> /
/// T3 超时后的 S9F9 / 非法应答后的 S9F7），不带 W 位只发送；并发的在途事务与处理器里发消息。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsEquipmentSendTests
{
    private const ushort DeviceId = HsmsEquipmentRig.DefaultDeviceId;

    /// <summary>SEMI E5 里的 J（JIS-8）格式码：合法但本库不支持（计划第 0 节 P51-D3）。</summary>
    private const byte IllegalFormatByte = 0x45;

    [Fact(Timeout = 30000)]
    public async Task PrimaryWithTheWBit_GetsTheReplyTheHostSends()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // V-H-12 的形状：S5F1 W <L [3] <B 0x80> <U4 1001> <A "OVERHEAT">>，System Bytes 由设备端分配。
        SecsMessage primary = SecsMessage.Create(5, 1, replyExpected: true, SecsItem.L(SecsItem.B(0x80), SecsItem.U4(1001), SecsItem.A("OVERHEAT")));
        Task<SecsResult> sending = rig.Equipment.SendAsync(primary);
        HsmsHostFrame request = await host.NextFrameAsync(5000);

        AssertFrame(request, "00 00 00 1F 00 01 85 01 00 00 00 00 01 01 01 03 21 01 80 B1 04 00 00 03 E9 41 08 4F 56 45 52 48 45 41 54");

        // V-H-13：S5F2 <B 0x00>，System Bytes 原样回送请求的号。
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(5, 2, replyExpected: false, SecsItem.B(0x00)), DeviceId, request.Message.SystemBytes));

        SecsResult result = await Arrival.WithinAsync(sending, 5000);
        Assert.True(result.IsSuccess);
        Assert.Equal("<B 0x00>", result.Reply!.Body!.ToSml());
        Assert.Null(result.ErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task AReplyWithFunctionZero_IsAnAbort()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        Task<SecsResult> sending = rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        HsmsHostFrame request = await host.NextFrameAsync(5000);
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(5, 0, replyExpected: false), DeviceId, request.Message.SystemBytes));

        SecsResult result = await Arrival.WithinAsync(sending, 5000);
        Assert.False(result.IsSuccess);
        Assert.Equal(SecsError.Aborted, result.Error);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task ARejectFromTheHost_IsReportedWithItsReason()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        Task<SecsResult> sending = rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        HsmsHostFrame request = await host.NextFrameAsync(5000);
        await host.SendAsync(HsmsControl.Reject(DeviceId, 0, HsmsRejectReason.NotSelected, request.Message.SystemBytes).ToArray());

        SecsResult result = await Arrival.WithinAsync(sending, 5000);
        Assert.False(result.IsSuccess);
        Assert.Equal(SecsError.Rejected, result.Error);
        Assert.Equal((byte)HsmsRejectReason.NotSelected, result.RejectReason);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task NoReplyWithinT3_SendsS9F9AndReportsAReplyTimeout()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync(o => o.T3 = 400);
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // V-H-16 的形状：S9F9 的正文是我方主消息的 10 字节头（S6F11 W）。
        SecsResult result = await Arrival.WithinAsync(rig.Equipment.SendAsync(SecsMessage.Create(6, 11, replyExpected: true)), 5000);

        Assert.False(result.IsSuccess);
        Assert.Equal(SecsError.ReplyTimeout, result.Error);
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);

        HsmsHostFrame request = await host.NextFrameAsync(5000);
        Assert.Equal(6, request.Message.Stream);
        Assert.Equal(11, request.Message.Function);

        HsmsHostFrame notice = await host.NextFrameAsync(5000);
        Assert.Equal(9, notice.Message.Stream);
        Assert.Equal(9, notice.Message.Function);
        Assert.False(notice.Message.ReplyExpected);
        Assert.Equal(
            HsmsTestFrames.Encode(SecsItem.B(HsmsTestFrames.HeaderBytes(request.Bytes))),
            notice.Message.Body.ToArray());

        // T3 是事务失败，不是链路失败（ResetOnRequestTimeout = false）：会话仍然选中，链路照常往返。
        Assert.Equal(HsmsState.Selected, rig.State);
        await host.SendAsync(HsmsControl.LinktestRequest(12).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 0C", (await host.NextFrameAsync(5000)).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task AnIllegalReply_SendsS9F7AndReportsAnInvalidReply()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        Task<SecsResult> sending = rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        HsmsHostFrame request = await host.NextFrameAsync(5000);

        // S5F2 的正文是非法的：把库自己写出的正文的第一个字节改成非法的格式码 0x45（J），编码器不会产出它。
        byte[] reply = HsmsMessage.Write(SecsMessage.Create(5, 2, replyExpected: false, SecsItem.B(0x00)), DeviceId, request.Message.SystemBytes);
        reply[HsmsMessage.LengthPrefixBytes + HsmsMessage.HeaderBytes] = IllegalFormatByte;
        await host.SendAsync(reply);

        SecsResult result = await Arrival.WithinAsync(sending, 5000);
        Assert.False(result.IsSuccess);
        Assert.Equal(SecsError.InvalidReply, result.Error);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);

        HsmsHostFrame notice = await host.NextFrameAsync(5000);
        Assert.Equal(9, notice.Message.Stream);
        Assert.Equal(7, notice.Message.Function);
    }

    [Fact(Timeout = 30000)]
    public async Task AReplyThatIsNotTheExpectedFunction_IsReportedAsInvalid()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        Task<SecsResult> sending = rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        HsmsHostFrame request = await host.NextFrameAsync(5000);

        // S5F4 不是 S5F1 的应答（应答必须是 Function + 1）。
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(5, 4, replyExpected: false), DeviceId, request.Message.SystemBytes));

        SecsResult result = await Arrival.WithinAsync(sending, 5000);
        Assert.Equal(SecsError.InvalidReply, result.Error);
    }

    [Fact(Timeout = 30000)]
    public async Task APrimaryWithoutTheWBit_IsSentAndReportsNoReply()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        SecsResult result = await rig.Equipment.SendAsync(SecsMessage.Create(5, 5, replyExpected: false, SecsItem.A("EQ01")));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Reply);

        HsmsHostFrame request = await host.NextFrameAsync(5000);
        AssertFrame(request, "00 00 00 10 00 01 05 05 00 00 00 00 00 01 41 04 45 51 30 31");
    }

    [Fact(Timeout = 30000)]
    public async Task TwoPrimariesInFlight_GetTheirOwnRepliesEvenWhenTheHostAnswersInReverseOrder()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        Task<SecsResult> first = rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        Task<SecsResult> second = rig.Equipment.SendAsync(SecsMessage.Create(5, 3, replyExpected: true));

        HsmsHostFrame firstRequest = await host.NextFrameAsync(5000);
        HsmsHostFrame secondRequest = await host.NextFrameAsync(5000);
        Assert.Equal(1, firstRequest.Message.Function);
        Assert.Equal(3, secondRequest.Message.Function);

        // 主机倒序回应答：关联键是 System Bytes（P51-D7）。
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(5, 4, replyExpected: false, SecsItem.U4(2)), DeviceId, secondRequest.Message.SystemBytes));
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(5, 2, replyExpected: false, SecsItem.U4(1)), DeviceId, firstRequest.Message.SystemBytes));

        SecsResult[] results = await Task.WhenAll(first, second);
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess);
        Assert.Equal("<U4 1>", results[0].Reply!.Body!.ToSml());
        Assert.Equal("<U4 2>", results[1].Reply!.Body!.ToSml());
    }

    [Fact(Timeout = 30000)]
    public async Task AHandlerCanSendAndWaitForItsOwnReply()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        var nested = new TaskCompletionSource<SecsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Equipment.SetHandler(1, 1, async (primary, token) =>
        {
            // 处理器里发一条 S5F1 W 并等应答：应答由解析线程直接认领，不会死锁（P51-D13）。
            SecsResult inner = await rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
            nested.TrySetResult(inner);
            return primary.CreateReply(SecsItem.U4(1));
        });

        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, 1));
        HsmsHostFrame request = await host.NextFrameAsync(5000);

        Assert.Equal(5, request.Message.Stream);
        Assert.Equal(1, request.Message.Function);
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(5, 2, replyExpected: false, SecsItem.U4(42)), DeviceId, request.Message.SystemBytes));

        SecsResult nestedResult = await Arrival.WithinAsync(nested.Task, 5000);
        Assert.Equal("<U4 42>", nestedResult.Reply!.Body!.ToSml());
        Assert.Equal("00 00 00 10 00 01 01 02 00 00 00 00 00 01 B1 04 00 00 00 01", (await host.NextFrameAsync(5000)).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task WhileNotSelected_SendingIsRefusedAndNothingIsSent()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.ConnectAsync(rig);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotSelected, 5000);

        SecsResult result = await rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));

        Assert.False(result.IsSuccess);
        Assert.Equal(SecsError.NotSelected, result.Error);
        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);

        // 什么都没发出去：随后发一条 Linktest.req，主机收到的第一帧是它的应答。
        await host.SendAsync(HsmsControl.LinktestRequest(7).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 07", (await host.NextFrameAsync(5000)).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task AReplyOrANullPrimary_IsRejectedAtTheEntry()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Equipment.SendAsync(null!));

        // 应答只能由处理器返回（计划第 10 节第 2 条）。
        SecsResult result = await rig.Equipment.SendAsync(SecsMessage.Create(5, 2, replyExpected: false));

        Assert.False(result.IsSuccess);
        Assert.Equal(SecsError.InvalidRequest, result.Error);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    private static void AssertFrame(HsmsHostFrame frame, string vector)
        => Assert.Equal(HsmsTestFrames.WithSystemBytes(vector, frame.Message.SystemBytes), frame.Text);
}
