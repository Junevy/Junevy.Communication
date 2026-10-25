using System.Buffers.Binary;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Secs;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Testing;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Secs;

/// <summary>
/// SECS/HSMS 设备端页与最小 HSMS 主机的端到端测试（计划 Task 15 第 2 条）：选择会话；主机发 S1F1 W 时规则应答 S1F2；
/// 设备端发 S5F1 W 时主机回 S5F2；主机不应答时 T3 超时并收到 S9F9；没有规则的 S/F 收到 S9F5（同一 Stream 有规则）或 S9F3（没有）。
/// 报文与 P5.1 计划第 17.1 节的向量逐字节核对。
/// </summary>
public sealed class SecsHsmsEndToEndTests
{
    private const ushort DeviceId = 1;
    private const string ReplySml = "<L [2]\n  <A \"EQ01\">\n  <A \"1.0.0\">\n>";
    private const string AlarmSml = "<L [2]\n  <B 0x80>\n  <U4 1001>\n>";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SelectingTheSession_ShowsSelectedOnThePage()
    {
        var dispatcher = new ManualUiDispatcher();
        int port = FreePort.Get();
        await using var host = new MiniHsmsHost(port);
        await host.StartAsync();
        await using var page = CreateActivePage(dispatcher, port);

        await page.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => page.HsmsStateText == "已选择", Wait, dispatcher.RunPending);

        Assert.Equal(SessionState.Running, page.State);
        HsmsFrame select = await host.NextAsync();
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x0A, 0xFF, 0xFF, 0x00, 0x00, 0x00, HsmsFrames.SelectRequestType }, select.Data[..10]);
    }

    [Fact]
    public async Task AHostsS1F1_IsAnsweredByTheRule_WithS1F2()
    {
        var dispatcher = new ManualUiDispatcher();
        int port = FreePort.Get();
        await using var host = new MiniHsmsHost(port);
        await host.StartAsync();
        await using var page = CreateActivePage(dispatcher, port);
        SetRule(page, 1, 1, SecsRuleKind.Reply, ReplySml);
        await page.ConnectCommand.ExecuteAsync(null);
        HsmsFrame select = await host.NextAsync();

        await host.SendAsync(select.SessionId, HsmsFrames.Data(1, 1, replyExpected: true, DeviceId, 0x20));

        HsmsFrame reply = await host.NextAsync();
        Assert.Equal(
            HsmsFrames.Data(1, 2, replyExpected: false, DeviceId, 0x20, 0x01, 0x02, 0x41, 0x04, 0x45, 0x51, 0x30, 0x31, 0x41, 0x05, 0x31, 0x2E, 0x30, 0x2E, 0x30),
            reply.Data);
        await Waiting.UntilAsync(() => page.ReceivedMessages.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal("S1F1 W", page.ReceivedMessages[0].Header);
    }

    [Fact]
    public async Task AnEquipmentsS5F1WithW_IsAnsweredByTheHost_AndTheReplyShowsOnThePage()
    {
        var dispatcher = new ManualUiDispatcher();
        int port = FreePort.Get();
        await using var host = new MiniHsmsHost(port);
        await host.StartAsync();
        await using var page = CreateActivePage(dispatcher, port);
        await page.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => page.HsmsStateText == "已选择", Wait, dispatcher.RunPending);
        await host.NextAsync();
        page.SendStream = 5;
        page.SendFunction = 1;
        page.SendReplyExpected = true;
        page.SendSml = AlarmSml;

        Task send = page.SendCommand.ExecuteAsync(null);
        HsmsFrame alarm = await host.NextAsync();
        Assert.Equal((5, 1, true), (alarm.Stream, alarm.Function, alarm.ReplyExpected));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x21, 0x01, 0x80, 0xB1, 0x04, 0x00, 0x00, 0x03, 0xE9 }, alarm.Body);
        await host.SendAsync(alarm.SessionId, HsmsFrames.Data(5, 2, replyExpected: false, DeviceId, alarm.SystemBytes, 0x21, 0x01, 0x00));
        await send;

        Assert.Equal(("成功", "S5F2\n<B 0x00>\n."), (page.SendMessage, page.ReplySml));
        OperationRecord record = page.Operations[0];
        Assert.Equal(("发送", true), (record.Operation, record.Succeeded));
    }

    [Fact]
    public async Task AHostThatDoesNotAnswer_TimesOutAfterT3_AndTheEquipmentSendsS9F9()
    {
        var dispatcher = new ManualUiDispatcher();
        int port = FreePort.Get();
        await using var host = new MiniHsmsHost(port);
        await host.StartAsync();
        await using var page = CreateActivePage(dispatcher, port, t3: 300);
        await page.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => page.HsmsStateText == "已选择", Wait, dispatcher.RunPending);
        await host.NextAsync();
        page.SendStream = 5;
        page.SendFunction = 1;
        page.SendReplyExpected = true;
        page.SendSml = AlarmSml;

        Task send = page.SendCommand.ExecuteAsync(null);
        HsmsFrame alarm = await host.NextAsync();
        HsmsFrame timeout = await host.NextAsync();
        await send;

        Assert.Equal((9, 9), (timeout.Stream, timeout.Function));
        Assert.Equal(new byte[] { 0x21, 0x0A }.Concat(alarm.Header).ToArray(), timeout.Body);
        Assert.StartsWith("T3 超时", page.SendMessage);
        Assert.Equal(CommErrorKind.Timeout, page.Operations[0].ErrorKind);
    }

    [Fact]
    public async Task APrimaryOfAStreamWithRules_ButNotItsFunction_GetsS9F5_AndIsNotShownOnThePage()
    {
        var dispatcher = new ManualUiDispatcher();
        int port = FreePort.Get();
        await using var host = new MiniHsmsHost(port);
        await host.StartAsync();
        await using var page = CreateActivePage(dispatcher, port);
        SetRule(page, 1, 1, SecsRuleKind.Reply, ReplySml);
        await page.ConnectCommand.ExecuteAsync(null);
        HsmsFrame select = await host.NextAsync();
        byte[] request = HsmsFrames.Data(1, 13, replyExpected: true, DeviceId, 0x30);

        await host.SendAsync(select.SessionId, request);

        HsmsFrame s9 = await host.NextAsync();
        Assert.Equal((9, 5), (s9.Stream, s9.Function));
        Assert.Equal(new byte[] { 0x21, 0x0A }.Concat(request[4..14]).ToArray(), s9.Body);
        dispatcher.RunPending();
        Assert.DoesNotContain(page.ReceivedMessages, message => message.Header == "S1F13 W");
    }

    [Fact]
    public async Task APrimaryOfAStreamWithNoRules_GetsS9F3()
    {
        var dispatcher = new ManualUiDispatcher();
        int port = FreePort.Get();
        await using var host = new MiniHsmsHost(port);
        await host.StartAsync();
        await using var page = CreateActivePage(dispatcher, port);
        SetRule(page, 1, 1, SecsRuleKind.Reply, ReplySml);
        await page.ConnectCommand.ExecuteAsync(null);
        HsmsFrame select = await host.NextAsync();
        byte[] request = HsmsFrames.Data(6, 11, replyExpected: true, DeviceId, 0x40);

        await host.SendAsync(select.SessionId, request);

        HsmsFrame s9 = await host.NextAsync();
        Assert.Equal((9, 3), (s9.Stream, s9.Function));
        Assert.Equal(new byte[] { 0x21, 0x0A }.Concat(request[4..14]).ToArray(), s9.Body);
    }

    [Fact]
    public async Task PassiveMode_ListensAndSelectsWhenTheHostConnects()
    {
        var dispatcher = new ManualUiDispatcher();
        int port = FreePort.Get();
        await using var page = CreateActivePage(dispatcher, port);
        page.Mode = HsmsConnectionMode.Passive;
        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, page.State);

        using var peer = new TcpClient("127.0.0.1", port);
        NetworkStream stream = peer.GetStream();
        await stream.WriteAsync(HsmsFrames.SelectRequestFrame(0x77));

        Assert.Equal(HsmsFrames.SelectResponseFrame(0x77), await ReadFrameAsync(stream));
        await Waiting.UntilAsync(() => page.HsmsStateText == "已选择", Wait, dispatcher.RunPending);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length);
        var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(length)];
        await stream.ReadExactlyAsync(body);
        return [.. length, .. body];
    }

    private static SecsSessionViewModel CreateActivePage(ManualUiDispatcher dispatcher, int port, int t3 = 45000)
    {
        var page = new SecsSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Secs), FakeServices.Create(dispatcher));
        page.Mode = HsmsConnectionMode.Active;
        page.Host = "127.0.0.1";
        page.Port = port;
        page.DeviceId = DeviceId;
        page.T3 = t3;
        return page;
    }

    private static void SetRule(SecsSessionViewModel page, int stream, int function, SecsRuleKind kind, string sml)
    {
        page.NewRuleStream = stream;
        page.NewRuleFunction = function;
        page.NewRuleKind = kind;
        page.NewRuleSml = sml;
        page.AddRuleCommand.Execute(null);
    }
}
