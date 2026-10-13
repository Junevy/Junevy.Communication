using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 停止、启动与释放（计划 Task 6，P51-D15、D19，以及第 1 节第 12 条要求的多轮与竞态路径）：
/// 停止会发 Separate.req、停止不是释放、释放后每个公开方法的行为、处理器运行中与释放并发的路径。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsEquipmentLifecycleTests
{
    private const ushort DeviceId = HsmsEquipmentRig.DefaultDeviceId;

    [Fact(Timeout = 30000)]
    public async Task StopAsync_SendsSeparateAndThenSendingIsRefused()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        await Arrival.WithinAsync(rig.Equipment.StopAsync(), 10000);

        // P51-D15：已选中时先尽力发一条 Separate.req（V-H-6 的形状），不等应答。
        HsmsHostFrame separate = await host.NextFrameAsync(5000);
        Assert.Equal(HsmsTestFrames.WithSystemBytes("00 00 00 0A FF FF 00 00 00 09 00 00 00 08", separate.Message.SystemBytes), separate.Text);
        await host.AwaitPeerCloseAsync(5000);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotConnected, 5000);

        SecsResult result = await rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        Assert.Equal(SecsError.ConnectionLost, result.Error);

        // 可重复调用。
        await Arrival.WithinAsync(rig.Equipment.StopAsync(), 10000);
    }

    [Fact(Timeout = 60000)]
    public async Task StopThenStart_TwiceTheHostCanSelectAndExchangeAgain()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();

        for (int round = 1; round <= 2; round++)
        {
            await using HsmsHostConnector host = await HsmsEquipmentRig.ConnectAsync(rig);
            await host.SendAsync(HsmsControl.SelectRequest((uint)round).ToArray());
            Assert.Equal(HsmsControlKind.SelectResponse, (await host.NextFrameAsync(5000)).Message.Kind);
            await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000);

            rig.Equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply(SecsItem.U4(11))));
            await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, (uint)round));
            Assert.Equal(
                HsmsTestFrames.WithSystemBytes("00 00 00 10 00 01 01 02 00 00 00 00 00 00 B1 04 00 00 00 0B", (uint)round),
                (await host.NextFrameAsync(5000)).Text);

            await Arrival.WithinAsync(rig.Equipment.StopAsync(), 10000);
            await host.AwaitPeerCloseAsync(5000);
            await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotConnected, 5000);

            Assert.True((await Arrival.WithinAsync(rig.Equipment.StartAsync(), 10000)).IsSuccess);
        }

        // 第二轮之后仍然是可用的（停止不是释放）。
        await using HsmsHostConnector last = await HsmsEquipmentRig.ConnectAsync(rig);
        await last.SendAsync(HsmsControl.SelectRequest(9).ToArray());
        Assert.Equal(HsmsControlKind.SelectResponse, (await last.NextFrameAsync(5000)).Message.Kind);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeWhileAPrimaryIsWaitingForItsReply_ReportsConnectionLostInsteadOfThrowing()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync(o => o.T3 = 10000);
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        Task<SecsResult> sending = rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        await host.NextFrameAsync(5000);

        await Arrival.WithinAsync(rig.Equipment.DisposeAsync().AsTask(), 10000);

        SecsResult result = await Arrival.WithinAsync(sending, 5000);
        Assert.False(result.IsSuccess);
        Assert.Equal(SecsError.ConnectionLost, result.Error);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeWhileAHandlerIsRunning_CancelsItsTokenAndWaitsForIt()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Equipment.SetHandler(1, 1, async (primary, token) =>
        {
            started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult(true);
            }

            // 释放已经等到这里：此后返回的应答不发送（P51-D19）。
            return primary.CreateReply(SecsItem.U4(1));
        });
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, 1));
        await Arrival.WithinAsync(started.Task, 5000);

        await Arrival.WithinAsync(rig.Equipment.DisposeAsync().AsTask(), 10000);

        Assert.True(await Arrival.WithinAsync(cancelled.Task, 1000));
        await host.AwaitPeerCloseAsync(5000);

        // 主机最多只看到 Separate.req（停止时的告别），处理器那一条 S1F2 不在里头。
        Assert.DoesNotContain(host.ReceivedTexts, text => text.Contains("01 02"));
    }

    [Fact(Timeout = 30000)]
    public async Task AHandlerCanStopTheEquipmentWithoutDeadlocking()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Equipment.SetHandler(1, 1, async (primary, token) =>
        {
            // P51-D19：处理器内部调用 StopAsync 时不等它自己，否则死锁。
            await rig.Equipment.StopAsync();
            stopped.TrySetResult(true);
            return primary.CreateReply(SecsItem.U4(1));
        });
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, 1));

        Assert.True(await Arrival.WithinAsync(stopped.Task, 5000));
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotConnected, 5000);
        await host.AwaitPeerCloseAsync(5000);
    }

    [Fact(Timeout = 30000)]
    public async Task AfterDispose_EveryPublicMemberBehavesAsDocumented()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);
        SecsMessageHandler handler = (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply());

        await rig.Equipment.DisposeAsync();
        await rig.Equipment.DisposeAsync();
        rig.Equipment.Dispose();
        await rig.Equipment.DisposeAsync();

        // 释放之后 StartAsync 抛 ObjectDisposedException（P51-D19）。
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Equipment.StartAsync());

        SecsResult result = await rig.Equipment.SendAsync(SecsMessage.Create(5, 1, replyExpected: true));
        Assert.Equal(SecsError.ConnectionLost, result.Error);
        Assert.Equal(HsmsState.NotConnected, rig.State);

        // 处理器表只改不抛。
        rig.Equipment.SetHandler(1, 1, handler);
        Assert.True(rig.Equipment.RemoveHandler(1, 1));
        Assert.False(rig.Equipment.RemoveHandler(1, 1));

        await Arrival.WithinAsync(rig.Equipment.StopAsync(), 10000);
    }

    [Fact(Timeout = 30000)]
    public async Task TheHandlerTable_ReplacesTheSameStreamAndFunctionAndRejectsReplies()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        int calls = 0;
        rig.Equipment.SetHandler(1, 1, (primary, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<SecsMessage?>(primary.CreateReply(SecsItem.U4(1)));
        });
        rig.Equipment.SetHandler(1, 1, (primary, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<SecsMessage?>(primary.CreateReply(SecsItem.U4(2)));
        });
        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);

        await host.SendAsync(HsmsMessage.Write(SecsMessage.Create(1, 1, replyExpected: true), DeviceId, 1));

        // 同一 S/F 再设置时替换，因此应答来自第二个处理器。
        HsmsHostFrame answer = await host.NextFrameAsync(5000);
        Assert.Equal("<U4 2>", SecsItemCodec.Decode(answer.Message.Body, HsmsTimers.MaxMessageLength).Data!.ToSml());
        Assert.Equal(1, calls);

        // 处理器只能注册主消息（奇数 Function）。
        Assert.Throws<ArgumentException>(() => rig.Equipment.SetHandler(1, 2, (primary, token) => Task.FromResult<SecsMessage?>(null)));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Equipment.SetHandler(128, 1, (primary, token) => Task.FromResult<SecsMessage?>(null)));
    }

    [Fact(Timeout = 30000)]
    public async Task StateChanged_ReportsTheOrderWithReasons_AndASubscriberCannotBreakTheStateMachine()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();
        var seen = new List<HsmsStateChangedEventArgs>();
        rig.Equipment.StateChanged += (sender, args) =>
        {
            seen.Add(args);
            throw new InvalidOperationException("A subscriber blew up.");
        };
        rig.Equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(primary.CreateReply()));

        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(rig, 1);
        await host.SendAsync(HsmsControl.SeparateRequest(2).ToArray());
        await host.AwaitPeerCloseAsync(5000);
        await Polling.WaitUntilAsync(() => rig.State == HsmsState.NotConnected, 5000);

        // 订阅者抛异常只影响它自己：状态仍然按顺序变化。
        Assert.Equal(3, seen.Count);
        Assert.Equal(new[] { HsmsState.NotSelected, HsmsState.Selected, HsmsState.NotConnected }, seen.Select(args => args.Current));
        Assert.All(seen, args => Assert.False(string.IsNullOrEmpty(args.Reason)));
        Assert.Contains(rig.Logger.GetEntries(LogLevel.Error), entry => entry.Exception is InvalidOperationException);
    }

    [Fact(Timeout = 30000)]
    public async Task StartAsync_IsRefusedWhileTheEquipmentIsAlreadyListening()
    {
        await using HsmsEquipmentRig rig = await HsmsEquipmentRig.StartAsync();

        CommResult second = await rig.Equipment.StartAsync();

        Assert.False(second.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, second.ErrorKind);
    }
}
