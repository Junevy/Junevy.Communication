using System.Collections.Concurrent;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// TCP 客户端页对库的 <see cref="TcpServer"/> 的端到端测试（计划 Task 5 第 2 条）：回环地址，端口由空闲端口探测得到。
/// 服务端与客户端使用同一份分帧映射，因此回显的帧与发送的负载逐字节相同。
/// </summary>
public sealed class TcpClientPageTests
{
    [Fact]
    public Task Framing_Raw_RoundTripsOneFrame() => RoundTripAsync(
        framing => framing.Mode = FramingMode.Raw,
        "hex:01 02 03",
        [0x01, 0x02, 0x03]);

    [Fact]
    public Task Framing_Delimiter_RoundTripsOneFrame_AndTheDelimiterIsNotDelivered() => RoundTripAsync(
        framing =>
        {
            framing.Mode = FramingMode.Delimiter;
            framing.Delimiters = "hex:0D 0A";
            framing.KeepDelimiter = false;
            framing.AppendDelimiterOnSend = true;
        },
        "hex:41 42",
        [0x41, 0x42]);

    [Fact]
    public Task Framing_FixedLength_RoundTripsOneFrame() => RoundTripAsync(
        framing =>
        {
            framing.Mode = FramingMode.FixedLength;
            framing.FixedLength = 4;
        },
        "hex:01 02 03 04",
        [0x01, 0x02, 0x03, 0x04]);

    [Fact]
    public Task Framing_LengthField_RoundTripsOneFrame_WithTheHeaderInThePayload() => RoundTripAsync(
        framing =>
        {
            framing.Mode = FramingMode.LengthField;
            framing.LengthFieldOffset = 0;
            framing.LengthFieldSize = 1;
            framing.LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian;
            framing.LengthAdjustment = 0;
            framing.InitialBytesToStrip = 0;
        },
        "hex:02 41 42",
        [0x02, 0x41, 0x42]);

    [Fact]
    public Task Framing_StartEnd_RoundTripsOneFrame_WithTheMarkersKept() => RoundTripAsync(
        framing =>
        {
            framing.Mode = FramingMode.StartEnd;
            framing.StartMarker = "hex:02";
            framing.EndMarker = "hex:03";
            framing.KeepMarkers = true;
        },
        "hex:02 41 42 03",
        [0x02, 0x41, 0x42, 0x03]);

    [Fact]
    public Task Framing_IdleGap_RoundTripsOneFrame() => RoundTripAsync(
        framing =>
        {
            framing.Mode = FramingMode.IdleGap;
            framing.GapTimeout = 20;
        },
        "hex:41 42 43",
        [0x41, 0x42, 0x43]);

    [Fact]
    public async Task Correlation_Sequential_TwoRequests_EachGetsItsOwnReply()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(1), (session, data) => session.SendAsync(data));
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        UseFixedLength(page.Settings.Framing, 1);
        await page.ConnectCommand.ExecuteAsync(null);

        page.PayloadText = "hex:01";
        await page.RequestCommand.ExecuteAsync(null);
        page.PayloadText = "hex:02";
        await page.RequestCommand.ExecuteAsync(null);

        Assert.Equal(new byte[] { 0x02 }, page.ReceivedFrames[0].Data);
        Assert.Equal(new byte[] { 0x01 }, page.ReceivedFrames[1].Data);
    }

    [Fact]
    public async Task Correlation_Matcher_ReturnsTheMatchingReply_AndShowsTheOthers()
    {
        var dispatcher = new ManualUiDispatcher();
        // 服务端对每个请求先发一个不匹配的帧（EE 00），再发匹配的帧（AA 与请求的第二个字节）。
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(2), async (session, data) =>
        {
            await session.SendAsync(new byte[] { 0xEE, 0x00 });
            await session.SendAsync(new byte[] { 0xAA, data[1] });
        });
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        UseFixedLength(page.Settings.Framing, 2);
        page.Settings.Correlation.Mode = CorrelationMode.Matcher;
        page.Settings.Correlation.MatchByPrefix = true;
        page.Settings.Correlation.ResponsePrefix = "hex:AA";
        await page.ConnectCommand.ExecuteAsync(null);

        page.PayloadText = "hex:01 07";
        await page.RequestCommand.ExecuteAsync(null);

        // 不匹配的帧经 FrameReceived 回到界面线程，尚未泵送时只有请求的应答。
        Assert.Equal(new byte[] { 0xAA, 0x07 }, Assert.Single(page.ReceivedFrames).Data);
        await Waiting.UntilAsync(() => page.ReceivedFrames.Count == 2, TimeSpan.FromSeconds(5), dispatcher.RunPending);
        Assert.Equal(new byte[] { 0xEE, 0x00 }, page.ReceivedFrames[0].Data);
    }

    [Fact]
    public async Task Correlation_Keyed_InterleavedRequests_MatchByKey_NotByArrivalOrder()
    {
        var dispatcher = new ManualUiDispatcher();
        var pending = new List<(ITcpSession Session, byte[] Data)>();
        // 服务端收到两个请求后，先回复键 2，再过 300 毫秒回复键 1：应答的顺序与请求的顺序相反。
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(3), async (session, data) =>
        {
            List<(ITcpSession Session, byte[] Data)>? batch = null;
            lock (pending)
            {
                pending.Add((session, data));
                if (pending.Count == 2)
                {
                    batch = [.. pending];
                    pending.Clear();
                }
            }

            if (batch is null)
            {
                return;
            }

            var second = batch.Single(request => request.Data[1] == 0x02);
            var first = batch.Single(request => request.Data[1] == 0x01);
            await second.Session.SendAsync(second.Data);
            await Task.Delay(300);
            await first.Session.SendAsync(first.Data);
        });
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        UseFixedLength(page.Settings.Framing, 3);
        page.Settings.Correlation.Mode = CorrelationMode.Keyed;
        await page.ConnectCommand.ExecuteAsync(null);

        // 键位于偏移 0、宽度 2（大端）：请求 "00 01 AA" 的键是 1，"00 02 BB" 的键是 2。
        page.PayloadText = "hex:00 01 AA";
        Task first = page.RequestCommand.ExecuteAsync(null);
        page.PayloadText = "hex:00 02 BB";
        Task second = page.RequestCommand.ExecuteAsync(null);

        await second.WaitAsync(TimeSpan.FromSeconds(5));
        // 若按到达顺序配对，键 1 的请求会在键 2 的应答到达时完成；按键配对时它必须仍在等待。
        Assert.False(first.IsCompleted, "键 2 的应答先到，键 1 的请求应继续等待自己的应答。");
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, page.Operations.Count(operation => operation.Operation == "请求" && operation.Succeeded));
    }

    [Fact]
    public async Task Receive_TakesTheNextFrame_AndShowsIt()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(1), (_, _) => Task.CompletedTask);
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        UseFixedLength(page.Settings.Framing, 1);
        await page.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => server.SessionCount == 1, TimeSpan.FromSeconds(5));

        Task receive = page.ReceiveCommand.ExecuteAsync(null);
        await Task.Delay(100);
        await server.BroadcastAsync(new byte[] { 0x7A });
        await receive.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 0x7A }, Assert.Single(page.ReceivedFrames).Data);
        Assert.True(Assert.Single(page.Operations, operation => operation.Operation == "接收").Succeeded);
        // 被 ReceiveAsync 取走的帧不应再经 FrameReceived 显示一次。
        dispatcher.RunPending();
        Assert.Single(page.ReceivedFrames);
    }

    [Fact]
    public async Task Handshake_SendsItsBytes_AndConnectsWhenTheReplyMatches()
    {
        var dispatcher = new ManualUiDispatcher();
        var received = new ConcurrentQueue<byte[]>();
        await using var server = await ChannelTestKit.StartServerAsync(new FramingEditor().ToOptions(), (session, data) =>
        {
            received.Enqueue(data);
            return session.SendAsync(new byte[] { 0x06 });
        });
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        page.Settings.Handshake.Enabled = true;
        page.Settings.Handshake.Send = "hex:02";
        page.Settings.Handshake.ExpectReply = true;
        page.Settings.Handshake.ExpectedReply = "hex:06";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Connected, page.State);
        Assert.Equal(new byte[] { 0x02 }, Assert.Single(received));
    }

    [Fact]
    public async Task Handshake_WithAWrongReply_FailsTheConnect_AndRecordsIt()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await ChannelTestKit.StartServerAsync(new FramingEditor().ToOptions(),
            (session, _) => session.SendAsync(new byte[] { 0x15 }));
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        page.Settings.Handshake.Enabled = true;
        page.Settings.Handshake.Send = "hex:02";
        page.Settings.Handshake.ExpectReply = true;
        page.Settings.Handshake.ExpectedReply = "hex:06";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal("连接", record.Operation);
        Assert.False(record.Succeeded);
        Assert.Equal(CommErrorKind.ProtocolViolation, record.ErrorKind);
    }

    [Fact]
    public async Task Heartbeat_Failures_TakeTheChannelToReconnecting_AndBackToConnected_OnceAnswered()
    {
        var dispatcher = new ManualUiDispatcher();
        bool answering = false;
        // 服务端只在 answering 为真之后应答心跳（负载 00 的应答是 01）。
        await using var server = await ChannelTestKit.StartServerAsync(new FramingEditor().ToOptions(), (session, data) =>
            Volatile.Read(ref answering) && data.SequenceEqual(new byte[] { 0x00 })
                ? session.SendAsync(new byte[] { 0x01 })
                : Task.CompletedTask);
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        page.Settings.Heartbeat.Enabled = true;
        page.Settings.Heartbeat.Payload = "hex:00";
        page.Settings.Heartbeat.ExpectedReply = "hex:01";
        page.Settings.Heartbeat.Interval = 100;
        page.Settings.Heartbeat.Timeout = 50;
        page.Settings.Heartbeat.MaxFailures = 2;
        page.Settings.Reconnect.Enabled = true;
        page.Settings.Reconnect.Mode = ReconnectMode.FixedInterval;
        page.Settings.Reconnect.Interval = 50;
        page.Settings.Reconnect.MaxAttempts = 0;
        await page.ConnectCommand.ExecuteAsync(null);

        await Waiting.UntilAsync(() => page.State == SessionState.Reconnecting, TimeSpan.FromSeconds(10), dispatcher.RunPending);
        Volatile.Write(ref answering, true);
        await Waiting.UntilAsync(() => page.State == SessionState.Connected, TimeSpan.FromSeconds(10), dispatcher.RunPending);
    }

    [Fact]
    public async Task Statistics_ShowEveryConnectionField_AfterATransfer()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(1), (session, data) => session.SendAsync(data));
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        UseFixedLength(page.Settings.Framing, 1);
        await page.ConnectCommand.ExecuteAsync(null);
        page.PayloadText = "hex:01";
        await page.RequestCommand.ExecuteAsync(null);

        // 统计文字每秒由 UiTicker 经界面派发刷新一次。
        await Waiting.UntilAsync(() => page.StatisticsText.Contains("接收 1 帧"), TimeSpan.FromSeconds(5), dispatcher.RunPending);
        Assert.Contains("发送 1 帧 / 1 字节（最后 ", page.StatisticsText);
        Assert.Contains("接收 1 帧 / 1 字节（最后 ", page.StatisticsText);
        Assert.Contains("连续心跳失败 0，连接于 ", page.StatisticsText);
        Assert.DoesNotContain("无", page.StatisticsText);
    }

    [Fact]
    public async Task Connect_WithAnInvalidEditorValue_IsRefused_WithoutCreatingAChannel()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.TcpPage(dispatcher, FreePort.Get());
        page.Settings.Timeouts.RequestTimeout = 0;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.False(record.Succeeded);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
        Assert.Equal("参数有误，请先修正标红的字段。", record.Message);
    }

    /// <summary>服务端与页签使用同一种分帧：配置一次，两边都执行，然后请求并检查回显的帧。</summary>
    private static async Task RoundTripAsync(Action<FramingEditor> configure, string payload, byte[] expected)
    {
        var dispatcher = new ManualUiDispatcher();
        var serverFraming = new FramingEditor();
        configure(serverFraming);
        await using var server = await ChannelTestKit.StartServerAsync(serverFraming.ToOptions(), (session, data) => session.SendAsync(data));
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        configure(page.Settings.Framing);

        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Connected, page.State);
        page.PayloadText = payload;
        await page.RequestCommand.ExecuteAsync(null);

        Assert.Equal(expected, Assert.Single(page.ReceivedFrames).Data);
    }

    private static void UseFixedLength(FramingEditor framing, int length)
    {
        framing.Mode = FramingMode.FixedLength;
        framing.FixedLength = length;
    }
}
