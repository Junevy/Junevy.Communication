using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 主动端握手（P51-D11）：设备端发 <c>Select.req</c>（超时 T6），状态 0 才算选中，
/// 非 0 或超时都是握手失败，通道按 T5 重连。主机是只做报文收发的最小监听方。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsActiveHandshakeTests
{
    [Fact(Timeout = 30000)]
    public async Task SelectRequest_ReachesTheHostAndStatusZeroSelectsTheSession()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectAndLinktest);
        await using HsmsActiveRig rig = HsmsActiveRig.Start(host.Port, HsmsTimers.Fast(t5: 150, t6: 400));

        Assert.True((await Arrival.WithinAsync(rig.Channel.ConnectAsync(), 10000)).IsSuccess);

        // V-H-1 的形状：会话号 0xFFFF、SType 1，System Bytes 由库里的进程级计数器产生。
        HsmsHostFrame request = await host.NextFrameAsync(5000);
        Assert.Equal(HsmsControlKind.SelectRequest, request.Message.Kind);
        Assert.Equal(HsmsMessage.AnySession, request.Message.SessionId);
        Assert.InRange(request.Message.SystemBytes, 1u, 0xFFFFu);
        Assert.Equal(
            HsmsTestFrames.WithSystemBytes("00 00 00 0A FF FF 00 00 00 01 00 00 00 01", request.Message.SystemBytes),
            request.Text);

        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.Selected, 5000);

        HsmsStateAsserts.SelectSequence(rig.States);
        Assert.True(rig.Channel.IsConnected);
    }

    [Fact(Timeout = 40000)]
    public async Task AfterTheHostDropsTheSession_ARejectedSelectKeepsTheEquipmentRetryingPerT5()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectThenRejectWithStatus(2));
        await using HsmsActiveRig rig = HsmsActiveRig.Start(host.Port, HsmsTimers.Fast(t5: 150, t6: 400));

        Assert.True((await Arrival.WithinAsync(rig.Channel.ConnectAsync(), 10000)).IsSuccess);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.Selected, 5000);

        // 主机断开：设备端按 T5 重连，而重连时的 Select 被拒（状态 2）。
        host.DropConnection(0);
        await Polling.WaitUntilAsync(() => host.AcceptedCount >= 3, 20000);

        // 采样点可能落在一次重试的中间（那时是 NotSelected），但会话永远不会变成已选中。
        Assert.NotEqual(HsmsState.Selected, rig.Session.State);
        Assert.False(rig.Channel.IsConnected);
        Assert.Contains(rig.States.Changes, change => change.Current == HsmsState.Selected);
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("Select.rsp reports status 2"));
    }

    [Fact(Timeout = 40000)]
    public async Task AfterTheHostDropsTheSession_AnUnansweredSelectTimesOutAndIsRetriedPerT5()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectThenSilence());
        await using HsmsActiveRig rig = HsmsActiveRig.Start(host.Port, HsmsTimers.Fast(t5: 150, t6: 400));

        Assert.True((await Arrival.WithinAsync(rig.Channel.ConnectAsync(), 10000)).IsSuccess);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.Selected, 5000);

        host.DropConnection(0);

        // T6 = 400 毫秒：主机的第二次连接一直不应答，设备端必须等满 T6 之后才按 T5 重连再试。
        await TimingAssert.WithinAsync(
            TimeSpan.FromMilliseconds(350),
            TimeSpan.FromMilliseconds(15000),
            () => Polling.WaitUntilAsync(() => host.AcceptedCount >= 3, 20000));

        Assert.NotEqual(HsmsState.Selected, rig.Session.State);
        Assert.False(rig.Channel.IsConnected);
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("did not get a reply within 400 ms"));
    }

    [Fact(Timeout = 30000)]
    public async Task WhenTheHostAnswersWithSomethingElse_TheHandshakeFails()
    {
        // 主机把 Select.req 换成 Linktest.rsp 应答：不是 Select.rsp，握手必须失败，状态不能被当成已选中。
        using HsmsHostListener host = HsmsHostListener.Listen((connection, frame) =>
            HsmsControl.LinktestResponse(HsmsTestFrames.Parse(frame).SystemBytes).ToArray());
        await using HsmsActiveRig rig = HsmsActiveRig.Start(host.Port, HsmsTimers.Fast(t5: 10000));

        CommResult connected = await Arrival.WithinAsync(rig.Channel.ConnectAsync(), 10000);

        Assert.False(connected.IsSuccess);
        Assert.Equal(HsmsState.NotConnected, rig.Session.State);
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("SType 6"));
    }
}
