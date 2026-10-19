using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// TCP 服务端页（计划 Task 6 第 2 条）：与 TCP 客户端页互连（两个页签，回环地址），验证会话、发送、广播、
/// 自动应答、白名单、最大会话数，以及停止后再启动。两个页签共用同一个界面派发，由测试泵送。
/// </summary>
public sealed class TcpServerPageTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ClientPage_ConnectsToTheServerPage_AndItsFrameIsShownWithTheSession()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());
        await server.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, server.State);
        await using var client = ChannelTestKit.TcpPage(dispatcher, server.Settings.Port);
        await client.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => server.Sessions.Count == 1, Wait, dispatcher.RunPending);

        client.PayloadText = "hex:41 42";
        await client.SendCommand.ExecuteAsync(null);

        await Waiting.UntilAsync(() => server.ReceivedFrames.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal(new byte[] { 0x41, 0x42 }, server.ReceivedFrames[0].Data);
        Assert.Equal(server.Sessions[0].Source, server.ReceivedFrames[0].Source);
    }

    [Fact]
    public async Task Broadcast_ReachesTheClientPage()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await StartServerAsync(dispatcher);
        await using var client = await ConnectClientAsync(dispatcher, server);

        server.PayloadText = "hex:AA";
        await server.BroadcastCommand.ExecuteAsync(null);

        await Waiting.UntilAsync(() => client.ReceivedFrames.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal(new byte[] { 0xAA }, client.ReceivedFrames[0].Data);
        Assert.Contains(server.Operations, record => record.Operation == "广播" && record.Message == "已发送到 1 个会话。");
    }

    [Fact]
    public async Task SendToSelectedSession_ReachesOnlyThatClient()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await StartServerAsync(dispatcher);
        await using var first = await ConnectClientAsync(dispatcher, server);
        await using var second = await ConnectClientAsync(dispatcher, server, expectedSessions: 2);

        server.SelectedSession = server.Sessions[1];
        server.PayloadText = "hex:CC";
        await server.SendToSelectedCommand.ExecuteAsync(null);

        await Waiting.UntilAsync(() => second.ReceivedFrames.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal(new byte[] { 0xCC }, second.ReceivedFrames[0].Data);
        Assert.Empty(first.ReceivedFrames);
    }

    [Fact]
    public async Task CloseSelectedSession_DisconnectsOnlyThatClient()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await StartServerAsync(dispatcher);
        await using var first = await ConnectClientAsync(dispatcher, server);
        await using var second = await ConnectClientAsync(dispatcher, server, expectedSessions: 2);
        long remainingId = server.Sessions[1].Id;

        server.SelectedSession = server.Sessions[0];
        await server.CloseSelectedCommand.ExecuteAsync(null);

        await Waiting.UntilAsync(() => server.Sessions.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal(remainingId, server.Sessions[0].Id);
        await Waiting.UntilAsync(() => first.State != SessionState.Connected, Wait, dispatcher.RunPending);
        Assert.Equal(SessionState.Connected, second.State);
    }

    [Fact]
    public async Task AutoReply_Echo_AnswersTheRequest()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await StartServerAsync(dispatcher);
        server.AutoReplyMatch = string.Empty;
        server.AutoReplyEcho = true;
        server.AddAutoReplyCommand.Execute(null);
        Assert.Single(server.AutoReplies);
        await using var client = await ConnectClientAsync(dispatcher, server);

        client.PayloadText = "hex:01 02";
        await client.RequestCommand.ExecuteAsync(null);

        Assert.Equal(new byte[] { 0x01, 0x02 }, Assert.Single(client.ReceivedFrames).Data);
    }

    [Fact]
    public async Task AutoReply_MatchReply_AnswersOnlyTheMatchingFrames()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await StartServerAsync(dispatcher);
        server.AutoReplyMatch = "hex:AA";
        server.AutoReplyReply = "hex:BB";
        server.AutoReplyEcho = false;
        server.AddAutoReplyCommand.Execute(null);
        await using var client = await ConnectClientAsync(dispatcher, server);
        client.Settings.Timeouts.RequestTimeout = 300;

        client.PayloadText = "hex:AA 01";
        await client.RequestCommand.ExecuteAsync(null);
        client.PayloadText = "hex:01 02";
        await client.RequestCommand.ExecuteAsync(null);

        Assert.Equal(new byte[] { 0xBB }, Assert.Single(client.ReceivedFrames).Data);
        Assert.False(client.Operations[0].Succeeded, "没有匹配的规则，请求应当超时。");
    }

    [Fact]
    public async Task AddAutoReply_WithInvalidContent_IsRefusedAndRecorded()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());

        server.AutoReplyMatch = "hex:ZZ";
        server.AddAutoReplyCommand.Execute(null);
        server.AutoReplyMatch = string.Empty;
        server.AutoReplyEcho = false;
        server.AutoReplyReply = "hex:";
        server.AddAutoReplyCommand.Execute(null);

        Assert.Empty(server.AutoReplies);
        Assert.Equal(2, server.Operations.Count(record => !record.Succeeded && record.ErrorKind == CommErrorKind.InvalidRequest));
    }

    [Fact]
    public async Task AllowedRemoteAddresses_RefusesOtherAddresses()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());
        server.Settings.AllowedRemoteAddresses = "10.255.255.1";
        await server.ConnectCommand.ExecuteAsync(null);
        await using var client = ChannelTestKit.TcpPage(dispatcher, server.Settings.Port);

        await client.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => client.State != SessionState.Connected, Wait, dispatcher.RunPending);

        dispatcher.RunPending();
        Assert.Empty(server.Sessions);
    }

    [Fact]
    public async Task MaxSessions_RefusesTheClientsBeyondTheLimit()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());
        server.Settings.MaxSessions = 1;
        await server.ConnectCommand.ExecuteAsync(null);
        await using var first = await ConnectClientAsync(dispatcher, server);
        await using var second = ChannelTestKit.TcpPage(dispatcher, server.Settings.Port);

        await second.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => second.State != SessionState.Connected, Wait, dispatcher.RunPending);

        dispatcher.RunPending();
        Assert.Single(server.Sessions);
        Assert.Equal(SessionState.Connected, first.State);
    }

    [Fact]
    public async Task Stop_ClosesTheSessions_AndTheServerCanBeStartedAgain()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = await StartServerAsync(dispatcher);
        await using var first = await ConnectClientAsync(dispatcher, server);

        await server.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Stopped, server.State);
        Assert.Empty(server.Sessions);
        await Waiting.UntilAsync(() => first.State != SessionState.Connected, Wait, dispatcher.RunPending);

        await server.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, server.State);
        await using var second = await ConnectClientAsync(dispatcher, server);
        Assert.Equal(SessionState.Connected, second.State);
    }

    [Fact]
    public async Task Start_WithAnInvalidEditorValue_IsRefused()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());
        server.Settings.Port = 0;

        await server.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, server.State);
        OperationRecord record = Assert.Single(server.Operations);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
        Assert.Equal("参数有误，请先修正标红的字段。", record.Message);
    }

    /// <summary>创建并启动服务端页签（端口由空闲端口探测得到）。</summary>
    private static async Task<TcpServerSessionViewModel> StartServerAsync(ManualUiDispatcher dispatcher)
    {
        TcpServerSessionViewModel server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());
        await server.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, server.State);
        return server;
    }

    /// <summary>创建客户端页签并连接，等待服务端显示出期待数量的会话。</summary>
    private static async Task<TcpClientSessionViewModel> ConnectClientAsync(ManualUiDispatcher dispatcher, TcpServerSessionViewModel server, int expectedSessions = 1)
    {
        TcpClientSessionViewModel client = ChannelTestKit.TcpPage(dispatcher, server.Settings.Port);
        await client.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Connected, client.State);
        await Waiting.UntilAsync(() => server.Sessions.Count == expectedSessions, Wait, dispatcher.RunPending);
        return client;
    }
}
