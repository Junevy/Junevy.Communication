using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

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

    [Fact(Timeout = 40000)]
    public async Task AfterTheHostSendsSeparate_ItReconnectsPerT5AndSelectsAgain()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectAndLinktest);
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port);
        Assert.Equal(HsmsControlKind.SelectRequest, (await host.NextFrameAsync(5000)).Message.Kind);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

        // P51-D12：Separate.req 不应答、立即断开。主动端仍是"已启动"的连接发起方，因此按 T5 再连（P51-D9），
        // 而不是停在"已启动却永远不再连接"的状态（那样只能先停止再启动）。
        await host.SendAsync(HsmsControl.SeparateRequest(2).ToArray());

        await Polling.WaitUntilAsync(() => host.AcceptedCount >= 2, 10000);
        HsmsHostFrame again = await host.NextFrameAsync(10000);
        Assert.Equal(HsmsControlKind.SelectRequest, again.Message.Kind);
        Assert.Equal(1, again.Connection);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 10000);
    }

    [Fact(Timeout = 40000)]
    public async Task AfterASeparate_AFailedReconnectKeepsRetryingPerT5()
    {
        // 第一条连接选中成功，之后的 Select 一律被拒（状态 2）：Separate 之后那一次再连失败了，通道仍按 T5 继续试。
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectThenRejectWithStatus(2));
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

        await host.SendAsync(HsmsControl.SeparateRequest(2).ToArray());

        await Polling.WaitUntilAsync(() => host.AcceptedCount >= 3, 20000);
        Assert.NotEqual(HsmsState.Selected, rig.State);
    }

    [Fact(Timeout = 30000)]
    public async Task StoppingRightAfterASeparate_DoesNotConnectAgain()
    {
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectAndLinktest);
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port, o => o.T5 = 1000);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

        await host.SendAsync(HsmsControl.SeparateRequest(2).ToArray());
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotConnected, 5000);

        // 停止发生在 T5 的等待之内：停止要立刻完成，之后也不再连接。
        await Arrival.WithinAsync(rig.Equipment.StopAsync(), 3000);
        await Task.Delay(1500);
        Assert.Equal(1, host.AcceptedCount);
        Assert.Equal(HsmsState.NotConnected, rig.State);
    }

    [Fact(Timeout = 30000)]
    public async Task ActiveMode_ConnectsToAHostName()
    {
        // 主动端的 Host 是对端地址：与其他 TCP 客户端一样交给通道解析。
        using HsmsHostListener host = HsmsHostListener.Listen(HsmsHostScripts.SelectAndLinktest);
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartActiveAsync(host.Port, o => o.Host = "localhost");

        Assert.True(rig.StartResult.IsSuccess, rig.StartResult.ErrorMessage);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposingWhileTheFirstSelectIsPending_EndsTheStartAtOnce()
    {
        // 主机只接受连接、从不应答，T6 取 20 秒：释放不能等满 T6，进行中的 StartAsync 返回失败结果。
        using SilentTcpServer silent = SilentTcpServer.Start();
        HsmsEquipmentOptions options = HsmsEquipmentRig.FastOptions();
        options.Mode = HsmsConnectionMode.Active;
        options.Port = silent.Port;
        options.T6 = 20000;
        var logger = new TestLogger();
        HsmsEquipment equipment = HsmsEquipment.Create(options, logger);

        Task<CommResult> starting = equipment.StartAsync();
        await Polling.WaitUntilAsync(() => equipment.State == HsmsState.NotSelected, 5000);

        await Arrival.WithinAsync(equipment.DisposeAsync().AsTask(), 3000);
        CommResult started = await Arrival.WithinAsync(starting, 3000);

        Assert.False(started.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, started.ErrorKind);
        Assert.Equal(HsmsState.NotConnected, equipment.State);
        Assert.Empty(logger.GetEntries(LogLevel.Error));
    }

    [Fact(Timeout = 30000)]
    public async Task CancellingTheStart_ReportsCancelledAndLeavesTheEquipmentStartable()
    {
        using SilentTcpServer silent = SilentTcpServer.Start();
        HsmsEquipmentOptions options = HsmsEquipmentRig.FastOptions();
        options.Mode = HsmsConnectionMode.Active;
        options.Port = silent.Port;
        options.T6 = 20000;
        var logger = new TestLogger();
        await using HsmsEquipment equipment = HsmsEquipment.Create(options, logger);
        using var cancellation = new CancellationTokenSource();

        Task<CommResult> starting = equipment.StartAsync(cancellation.Token);
        await Polling.WaitUntilAsync(() => equipment.State == HsmsState.NotSelected, 5000);
        cancellation.Cancel();

        // 调用方取消：返回 Cancelled 的失败结果（不抛、不记 Error），设备端回到未启动，可以再启动。
        CommResult started = await Arrival.WithinAsync(starting, 3000);
        Assert.Equal(CommErrorKind.Cancelled, started.ErrorKind);
        Assert.Equal(HsmsState.NotConnected, equipment.State);
        Assert.Empty(logger.GetEntries(LogLevel.Error));

        Task<CommResult> again = equipment.StartAsync();
        // 2026-10-25 全量并行（31 个测试宿主）时这里两次超时，放宽到 15 秒仍失败，说明不是慢；单独运行 25 次都通过，原因未查明。
        // 等待保持原值，超时时报告第二次启动的结果、设备端状态与告警，供下次复现时定位。
        try
        {
            await Polling.WaitUntilAsync(() => silent.AcceptedConnectionCount >= 2, 5000);
        }
        catch (TimeoutException)
        {
            string second = again.IsCompleted ? (await again).ToString() : "still running";
            string warnings = string.Join(" | ", logger.GetEntries(LogLevel.Warning).Select(entry => entry.Message));
            Assert.Fail($"The second start was not accepted: accepted={silent.AcceptedConnectionCount}, state={equipment.State}, "
                + $"second start={second}, warnings={warnings}");
        }
        await equipment.StopAsync();
        Assert.False((await Arrival.WithinAsync(again, 3000)).IsSuccess);
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
