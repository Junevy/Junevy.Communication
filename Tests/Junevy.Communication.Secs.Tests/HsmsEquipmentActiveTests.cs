using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 主动端设备（计划 Task 7，P51-D9、D11、D19）：设备端去连主机，发 <c>Select.req</c>，
/// 掉线按 T5 重连并重新 Select。主机是测试侧只做报文收发的监听方。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsEquipmentActiveTests
{
    private const ushort DeviceId = HsmsEquipmentRig.DefaultDeviceId;

    [Fact(Timeout = 30000)]
    public async Task ConnectsSelectsAndExchangesInBothDirections()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectAndLinktest);
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port);
        rig.Equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(
            primary.CreateReply(SecsItem.L(SecsItem.A("EQ01"), SecsItem.A("1.0.0")))));

        // 设备端先发 Select.req（V-H-1 的形状），主机回状态 0 之后才算选中。
        HsmsHostFrame select = await host.NextFrameAsync(5000);
        Assert.Equal(HsmsControlKind.SelectRequest, select.Message.Kind);
        Assert.Equal(HsmsMessage.AnySession, select.Message.SessionId);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

        // 主机 → 设备端：S1F1 W 走处理器得到 S1F2（V-H-11）。
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, 3));
        HsmsHostFrame answer = await host.NextFrameAsync(5000);
        Assert.Equal(1, answer.Message.Stream);
        Assert.Equal(2, answer.Message.Function);
        Assert.Equal(3u, answer.Message.SystemBytes);
        Assert.Equal("00 00 00 19 00 01 01 02 00 00 00 00 00 03 01 02 41 04 45 51 30 31 41 05 31 2E 30 2E 30", answer.Text);

        // 设备端 → 主机：S5F1 W 等 S5F2（V-H-12 / V-H-13）。
        Task<SecsResult> sending = rig.Equipment.SendAsync(
            SecsMessage.Create(5, 1, replyExpected: true, SecsItem.L(SecsItem.B(0x80), SecsItem.U4(1001))));
        HsmsHostFrame request = await host.NextFrameAsync(5000);
        Assert.Equal(5, request.Message.Stream);
        Assert.Equal(1, request.Message.Function);
        await host.SendAsync(HsmsMessage.Write(
            SecsMessage.Create(5, 2, replyExpected: false, SecsItem.B(0x00)), DeviceId, request.Message.SystemBytes));

        SecsResult result = await Arrival.WithinAsync(sending, 5000);
        Assert.True(result.IsSuccess);
        Assert.Equal("<B 0x00>", result.Reply!.Body!.ToSml());
    }

    [Fact(Timeout = 40000)]
    public async Task AfterTheHostDropsTheSession_ItReconnectsAndSelectsAgainPerT5()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectAndLinktest);
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port);

        // 第一条连接上先选中。
        HsmsHostFrame first = await host.NextFrameAsync(5000);
        Assert.Equal(HsmsControlKind.SelectRequest, first.Message.Kind);
        Assert.Equal(0, first.Connection);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

        // 主机断开第一条连接：设备端按 T5 重连，并重新走一遍 Select。
        host.DropConnection(0);

        await Polling.WaitUntilAsync(() => host.AcceptedCount >= 2, 10000);
        HsmsHostFrame again = await host.NextFrameAsync(10000);
        Assert.Equal(HsmsControlKind.SelectRequest, again.Message.Kind);
        Assert.Equal(1, again.Connection);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 10000);

        // 第二条连接上同样能收发。
        rig.Equipment.SetHandler(1, 3, (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply(SecsItem.U4(7))));
        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 3, replyExpected: true), DeviceId, 4));

        HsmsHostFrame answer = await host.NextFrameAsync(5000);
        Assert.Equal(1, answer.Message.Stream);
        Assert.Equal(4, answer.Message.Function);
        Assert.Equal(4u, answer.Message.SystemBytes);
        Assert.Equal("<U4 7>", SecsItemCodec.Decode(answer.Message.Body, HsmsTimers.MaxMessageLength).Data!.ToSml());
    }

    [Fact(Timeout = 40000)]
    public async Task ARejectedSelect_KeepsTheEquipmentRetryingPerT5()
    {
        // 第一条连接选中成功；之后主机的 Select 一直被拒（状态 2）。
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectThenRejectWithStatus(2));
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

        host.DropConnection(0);

        // 每次重连都收到状态 2，设备端因此一直重试（至少再试两次）。
        await Polling.WaitUntilAsync(() => host.AcceptedCount >= 3, 20000);
        Assert.NotEqual(HsmsState.Selected, rig.State);
    }

    [Fact(Timeout = 40000)]
    public async Task AfterStopThenStart_TheEquipmentStillReconnectsAndSelectsAgain()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectAndLinktest);
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

        int connected = host.AcceptedCount;
        await Arrival.WithinAsync(rig.Equipment.StopAsync(), 10000);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotConnected, 5000);

        // 停止期间不该有新的连接：T5 是 200 毫秒，等一会儿再启动。
        Assert.Equal(connected, host.AcceptedCount);

        Assert.True((await Arrival.WithinAsync(rig.Equipment.StartAsync(), 10000)).IsSuccess);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 10000);
        int afterRestart = host.AcceptedCount;

        // P51-D19：重启之后再断一次，仍然按 T5 重连并重新 Select。
        host.DropConnection(afterRestart - 1);
        await Polling.WaitUntilAsync(() => host.AcceptedCount > afterRestart, 10000);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 10000);
    }

    [Fact(Timeout = 30000)]
    public async Task AHostThatAcceptsButNeverAnswers_MakesStartFailWithoutThrowing()
    {
        // 只接受连接、从不应答的主机：Select 在 T6 之后超时，StartAsync 返回失败结果。
        using SilentTcpServer silent = SilentTcpServer.Start();
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(silent.Port, o => o.T6 = 300);

        CommResult started = rig.StartResult;

        Assert.False(started.IsSuccess);
        Assert.Equal(HsmsState.NotConnected, rig.State);
        await Polling.WaitUntilAsync(() => silent.AcceptedConnectionCount >= 1, 5000);

        // 释放之后仍然安静：不抛异常，可重复调用。
        await rig.Equipment.DisposeAsync();
        await rig.Equipment.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task ActiveMode_RequiresTheHostAddress()
    {
        // P51-D12 第 1 条：主动端必须给出对端地址。
        HsmsEquipmentOptions options = HsmsEquipmentRig.FastOptions();
        options.Mode = HsmsConnectionMode.Active;
        options.Host = null;

        ArgumentException error = Assert.Throws<ArgumentException>(() => HsmsEquipment.Create(options));

        Assert.Contains(nameof(HsmsEquipmentOptions.Host), error.Message);
    }
}
