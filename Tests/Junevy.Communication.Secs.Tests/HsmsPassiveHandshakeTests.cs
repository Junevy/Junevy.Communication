using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 被动端握手（P51-D11）：在 T7 内循环等 <c>Select.req</c>，期间应答 Linktest、拒绝数据消息、断开 Separate。
/// 真实回环套接字，设备端是 <see cref="Junevy.Communication.Tcp.Server.TcpServer"/> + <see cref="HsmsPassiveHandshake"/>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsPassiveHandshakeTests
{
    private const ushort DeviceId = 1;

    [Fact(Timeout = 30000)]
    public async Task SelectRequest_IsAnsweredWithStatusZeroAndMovesTheSessionToSelected()
    {
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast());
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);

        // V-H-1：主机发起 Select，System Bytes 为 1。
        await host.SendAsync(HsmsControl.SelectRequest(1).ToArray());

        // V-H-2：设备端回 Select.rsp 状态 0，并原样回送对方的 System Bytes。
        Assert.Equal("00 00 00 0A FF FF 00 00 00 02 00 00 00 01", (await host.NextFrameAsync(5000)).Text);

        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.Selected, 5000);

        // P51-D10：TCP 连上即 NotSelected，Select 成功即 Selected；事件按发生顺序发出。
        HsmsStateAsserts.SelectSequence(rig.States);
        Assert.Equal(1, rig.Server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public async Task BeforeSelect_LinktestIsAnsweredAndDataMessagesAreRejectedWithReasonFour()
    {
        // T7 留足做三次交换的时间：中途不应该断开。
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(t7: 8000));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotSelected, 5000);

        // V-H-4 → V-H-5：未选中时 Linktest 照常应答。
        await host.SendAsync(HsmsControl.LinktestRequest(7).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 07", (await host.NextFrameAsync(5000)).Text);

        // V-H-10 → V-H-7：未选中时收到数据消息，回 Reject.req 原因 4（会话号回送被拒消息的设备号）。
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, 2));
        Assert.Equal("00 00 00 0A 00 01 00 04 00 07 00 00 00 02", (await host.NextFrameAsync(5000)).Text);

        // 拒绝之后握手仍然继续等：再 Select 一次就能选中。
        await host.SendAsync(HsmsControl.SelectRequest(3).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 02 00 00 00 03", (await host.NextFrameAsync(5000)).Text);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.Selected, 5000);

        // 期间没有额外的状态变化：只有 NotConnected → NotSelected → Selected。
        Assert.Equal(2, rig.States.Changes.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task DeselectBeforeSelect_IsRejectedWithReasonOne()
    {
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(t7: 8000));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotSelected, 5000);

        // V-H-8：Deselect.req（SType 3）在 HSMS-SS 里不用，回 Reject.req 原因 1，头字节 2 是被拒的 SType。
        await host.SendAsync(HsmsControl.DeselectRequest(9).ToArray());
        Assert.Equal("00 00 00 0A FF FF 03 01 00 07 00 00 00 09", (await host.NextFrameAsync(5000)).Text);

        // 拒绝之后仍在等 Select。
        Assert.Equal(HsmsState.NotSelected, rig.Session.State);
    }

    [Fact(Timeout = 30000)]
    public async Task AFrameWithAnUnsupportedPType_IsRejectedWithReasonTwoAndTheHandshakeKeepsWaiting()
    {
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(t7: 8000));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotSelected, 5000);

        // PType 不是 0 → Reject.req 原因 2，头字节 2 是那个 PType（SEMI E37、V-H-9 的形状）。
        // 这一帧的 SType 恰好是 1：不能因此被当成 Select.req。
        await host.SendAsync(HexVector.ToBytes("00 00 00 0A FF FF 00 00 05 01 00 00 00 0A"));
        Assert.Equal("00 00 00 0A FF FF 05 02 00 07 00 00 00 0A", (await host.NextFrameAsync(5000)).Text);

        // 握手继续等：随后的 Linktest.req 照常应答，状态仍是 NotSelected。
        await host.SendAsync(HsmsControl.LinktestRequest(11).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 0B", (await host.NextFrameAsync(5000)).Text);
        Assert.Equal(HsmsState.NotSelected, rig.Session.State);
    }

    [Fact(Timeout = 30000)]
    public async Task ASilentHostWithinT7_KeepsTheHandshakeWaiting()
    {
        // 测试装置把 RequestTimeout 调小到 400 毫秒（通道默认是 2000），因此"握手等一帧"只要被
        // 通道的默认请求超时打断，这里就会在两倍于它的沉默之后露馅。
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(t7: 8000));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotSelected, 5000);

        // 主机沉默的时间超过通道默认的请求超时，但仍远在 T7 之内：握手必须继续等。
        await Task.Delay(800);

        Assert.Equal(HsmsState.NotSelected, rig.Session.State);

        await host.SendAsync(HsmsControl.SelectRequest(1).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 02 00 00 00 01", (await host.NextFrameAsync(5000)).Text);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.Selected, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task FramesThatArriveWhileThePreviousOneIsBeingAnswered_AreAllHandled()
    {
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(t7: 8000));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotSelected, 5000);

        // 一口气发来 Linktest.req → Linktest.req → Select.req：后两条会在设备端应答上一条的同时到达，
        // 一条都不能丢（P51-D13 的"按到达顺序逐个处理"）。Select 之后握手就结束了，
        // 所以突发帧都放在 Select 之前。
        await host.SendAsync(HsmsControl.LinktestRequest(1).ToArray());
        await host.SendAsync(HsmsControl.LinktestRequest(2).ToArray());
        await host.SendAsync(HsmsControl.SelectRequest(3).ToArray());

        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 01", (await host.NextFrameAsync(5000)).Text);
        Assert.Equal("00 00 00 0A FF FF 00 00 00 06 00 00 00 02", (await host.NextFrameAsync(5000)).Text);
        Assert.Equal("00 00 00 0A FF FF 00 00 00 02 00 00 00 03", (await host.NextFrameAsync(5000)).Text);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.Selected, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task WithoutSelect_TheHandshakeTimesOutAfterT7AndTheSessionGoesBackToNotConnected()
    {
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(t7: 400));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotSelected, 5000);

        // T7 = 400 毫秒：断开既不能立刻发生（那是没在等），也不能太晚。
        await TimingAssert.WithinAsync(
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(6000),
            () => host.AwaitPeerCloseAsync(8000));

        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotConnected, 5000);
        Assert.Equal(HsmsState.NotSelected, rig.States.Changes[0].Current);
        Assert.Equal(HsmsState.NotConnected, rig.States.Changes[rig.States.Changes.Count - 1].Current);
    }

    [Fact(Timeout = 30000)]
    public async Task SeparateRequestBeforeSelect_EndsTheHandshakeWithoutAReply()
    {
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(t7: 8000));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotSelected, 5000);

        await host.SendAsync(HsmsControl.SeparateRequest(8).ToArray());

        // Separate 不应答（P51-D12），握手直接失败，通道随即断开。
        await host.AwaitPeerCloseAsync(5000);
        Assert.Equal(0, host.ReceivedCount);
        await Polling.WaitUntilAsync(() => rig.Session.State == HsmsState.NotConnected, 5000);
    }
}
