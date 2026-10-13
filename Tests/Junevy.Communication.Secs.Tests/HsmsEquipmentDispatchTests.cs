using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 设备端处理收到的主消息与控制消息（计划 Task 6，P51-D12、D13、S9 消息）：
/// 处理器往返、S9F1 / S9F3 / S9F5 / S9F7、<c>SxF0</c> 中止、不带 W 位的主消息，以及每一条控制消息的处理。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsEquipmentDispatchTests
{
    private const ushort DeviceId = HsmsEquipmentRig.DefaultDeviceId;

    [Fact(Timeout = 30000)]
    public async Task PrimaryWithTheWBit_IsAnsweredByTheHandlerWithTheOriginalSystemBytes()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        rig.Equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(
            primary.CreateReply(SecsItem.L(SecsItem.A("EQ01"), SecsItem.A("1.0.0")))));
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 3);

        // V-H-10：S1F1 W（设备号 1、System Bytes 3）。
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, 3));

        // V-H-11：处理器返回的 S1F2，原样回送主消息的 System Bytes。
        Assert.Equal(
            "00 00 00 19 00 01 01 02 00 00 00 00 00 03 01 02 41 04 45 51 30 31 41 05 31 2E 30 2E 30",
            (await host.NextFrameAsync(5000)).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task PrimaryForAnotherDeviceId_IsAnsweredWithS9F1()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        rig.Equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply()));
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // ① 会话号不是设备号 → S9F1，不回应答（SEMI E5）。
        byte[] offending = HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), 2, 6);
        await host.SendAsync(offending);

        // V-H-17：正文是被拒消息的 10 字节头（会话号 2、W 位、S1F1）。
        AssertSystemMessage(await host.NextFrameAsync(5000), 1, offending);
    }

    [Fact(Timeout = 30000)]
    public async Task PrimaryOnAnUnknownStream_IsAnsweredWithS9F3()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        rig.Equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply()));
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // ③ 这个 Stream 一个处理器都没有 → S9F3。
        byte[] offending = HsmsMessage.Write(SecsMessage.Create(7, 1, replyExpected: true), DeviceId, 5);
        await host.SendAsync(offending);

        AssertSystemMessage(await host.NextFrameAsync(5000), 3, offending);
    }

    [Fact(Timeout = 30000)]
    public async Task PrimaryWithAnUnknownFunction_IsAnsweredWithS9F5()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        rig.Equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply()));
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // ③ 这个 Stream 有处理器，但没有这个 Function → S9F5（V-H-14）。
        byte[] offending = HsmsMessage.Write(SecsMessage.Create(1, 99, replyExpected: true), DeviceId, 4);
        await host.SendAsync(offending);

        AssertSystemMessage(await host.NextFrameAsync(5000), 5, offending);
    }

    [Fact(Timeout = 30000)]
    public async Task PrimaryWithAnIllegalBody_IsAnsweredWithS9F7()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // ② 正文解码失败（这里是非法的格式字节 0x45 = J）→ S9F7（V-H-18）。报文手写：编码器不会产出非法正文。
        byte[] offending = HexVector.ToBytes("00 00 00 0D 00 01 82 29 00 00 00 00 00 07 45 01 41");
        await host.SendAsync(offending);

        AssertSystemMessage(await host.NextFrameAsync(5000), 7, offending);
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("cannot be decoded"));
    }

    [Theory(Timeout = 30000)]
    [InlineData("null", "returned no reply")]
    [InlineData("mismatched", "does not match the primary")]
    [InlineData("throws", "handler threw")]
    public async Task AHandlerThatCannotAnswer_AbortsTheTransactionWithSxF0(string behaviour, string logFragment)
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        rig.Equipment.SetHandler(2, 41, Handler(behaviour));
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 5);

        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(2, 41, replyExpected: true), DeviceId, 5));

        // V-H-15 的形状：中止事务的 S2F0（System Bytes 由设备端的计数器分配）。
        AssertFrame(await host.NextFrameAsync(5000), "00 00 00 0A 00 01 02 00 00 00 00 00 00 05");
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Error), entry => entry.Message.Contains(logFragment));
    }

    [Fact(Timeout = 30000)]
    public async Task APrimaryWithoutTheWBit_CallsTheHandlerAndSendsNoReply()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        var called = new TaskCompletionSource<SecsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Equipment.SetHandler(1, 3, (primary, token) =>
        {
            called.TrySetResult(primary);
            return Task.FromResult<SecsMessage?>(null);
        });
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 3, replyExpected: false, body: SecsItem.U4(7)), DeviceId, 6));

        SecsMessage primary = await Arrival.WithinAsync(called.Task, 5000);
        Assert.Equal("<U4 7>", primary.Body!.ToSml());

        // 没有应答：随后发一条 Linktest.req，主机收到的第一帧是它的应答。
        await host.SendAsync(HsmsControl.LinktestRequest(7).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 07", (await host.NextFrameAsync(5000)).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task APrimaryWithoutTheWBit_IgnoresTheReplyTheHandlerReturns()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        rig.Equipment.SetHandler(1, 3, (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply()));
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 3, replyExpected: false), DeviceId, 1));
        await host.SendAsync(HsmsControl.LinktestRequest(7).ToArray());

        // 不带 W 位时处理器返回的应答不发送（只记一条 Warning）。
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 07", (await host.NextFrameAsync(5000)).Text);
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("without the W bit"));
    }

    [Fact(Timeout = 30000)]
    public async Task WhileSelected_LinktestAndASecondSelectAreAnsweredAndDeselectIsRejected()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // P51-D12：Linktest.req → Linktest.rsp（V-H-4 / V-H-5 的形状）。
        await host.SendAsync(HsmsControl.LinktestRequest(2).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 02", (await host.NextFrameAsync(5000)).Text);

        // 重复的 Select.req → 状态 1（已激活），V-H-3 的形状。
        await host.SendAsync(HsmsControl.SelectRequest(3).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 01 00 02 00 00 00 03", (await host.NextFrameAsync(5000)).Text);

        // HSMS-SS 不用 Deselect → Reject.req 原因 1（V-H-8）。
        await host.SendAsync(HsmsControl.DeselectRequest(4).ToArray());
        Assert.Equal("00 00 00 0A FF FF 03 01 00 07 00 00 00 04", (await host.NextFrameAsync(5000)).Text);

        Assert.Equal(HsmsState.Selected, rig.State);
    }

    [Fact(Timeout = 30000)]
    public async Task WhileSelected_AnUnsolicitedResponseOrAnUnknownControlMessage_IsRejected()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // 没有对应在途事务的 Select.rsp → 原因 3。
        await host.SendAsync(HsmsControl.SelectResponse(9, 0).ToArray());
        Assert.Equal("00 00 00 0A FF FF 02 03 00 07 00 00 00 09", (await host.NextFrameAsync(5000)).Text);

        // 未分配的 SType（8）→ 原因 1，头字节 2 是那个 SType。
        await host.SendAsync(HexVector.ToBytes("00 00 00 0A FF FF 00 00 00 08 00 00 00 0A"));
        Assert.Equal("00 00 00 0A FF FF 08 01 00 07 00 00 00 0A", (await host.NextFrameAsync(5000)).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task WhileSelected_ALateDataReply_IsDroppedWithAWarning()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // 偶数 Function 的数据消息取应答键（P51-D7）；没有在途事务时它作为主动消息到达派发：迟到应答，记 Warning 并丢弃。
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(6, 12, replyExpected: false), DeviceId, 8));

        await host.SendAsync(HsmsControl.LinktestRequest(7).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 07", (await host.NextFrameAsync(5000)).Text);
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("unsolicited data reply"));
    }

    [Fact(Timeout = 30000)]
    public async Task SeparateRequest_EndsTheSessionWithoutAReply_AndTheHostCanSelectAgain()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector first = await HsmsEquipmentRig.SelectAsync(rig, 1);

        await first.SendAsync(HsmsControl.SeparateRequest(2).ToArray());

        // P51-D12：Separate.req 不应答，立即断开。
        await first.AwaitPeerCloseAsync(5000);
        Assert.Equal(0, first.ReceivedCount);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotConnected, 5000);

        // 主机重连后可以再次 Select。
        await using HsmsHostConnector second = await HsmsEquipmentRig.ConnectAsync(rig);
        await second.SendAsync(HsmsControl.SelectRequest(3).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 02 00 00 00 03", (await second.NextFrameAsync(5000)).Text);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task WhileSelected_ASecondHostIsRejected()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector first = await HsmsEquipmentRig.SelectAsync(rig, 1);

        // MaxSessions = 1（HSMS-SS 单会话）：第二个主机在接入阶段就被关掉。
        await using HsmsHostConnector second = await HsmsEquipmentRig.ConnectAsync(rig);
        await second.AwaitPeerCloseAsync(5000);

        Assert.Equal(HsmsState.Selected, rig.State);
        await first.SendAsync(HsmsControl.LinktestRequest(9).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 09", (await first.NextFrameAsync(5000)).Text);
    }

    // System Bytes 由设备端的进程级计数器分配，比对时把向量里的号换成实际值（第 17 节的说明）。
    private static void AssertFrame(HsmsHostFrame frame, string vector)
        => Assert.Equal(HsmsTestFrames.WithSystemBytes(vector, frame.Message.SystemBytes), frame.Text);

    // S9 消息：Stream 9、Function 给定、不带 W 位，正文是被涉及消息的 10 字节头。
    // 逐字节比较正文而不是拼十六进制向量：S9 消息里有两个 System Bytes（它自己的与被涉及消息的），
    // 都不由主机决定（第 17.1 节的 V-H-14、V-H-16、V-H-17、V-H-18 正是这个形状）。
    private static void AssertSystemMessage(HsmsHostFrame frame, byte function, byte[] offending)
    {
        Assert.Equal(9, frame.Message.Stream);
        Assert.Equal(function, frame.Message.Function);
        Assert.False(frame.Message.ReplyExpected);
        Assert.Equal(
            HsmsTestFrames.Encode(SecsItem.B(HsmsTestFrames.HeaderBytes(offending))),
            frame.Message.Body.ToArray());
    }

    private static SecsMessageHandler Handler(string behaviour) => behaviour switch
    {
        "null" => (primary, token) => Task.FromResult<SecsMessage?>(null),
        "mismatched" => (primary, token) => Task.FromResult<SecsMessage?>(
            SecsMessage.Create(primary.Stream, (byte)(primary.Function + 2), replyExpected: false)),
        "throws" => (primary, token) => throw new InvalidOperationException("The handler blew up."),
        _ => throw new ArgumentOutOfRangeException(nameof(behaviour), behaviour, "Unknown behaviour."),
    };
}
