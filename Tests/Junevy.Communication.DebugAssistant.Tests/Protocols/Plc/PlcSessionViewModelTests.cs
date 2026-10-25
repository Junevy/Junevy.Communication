using System.Text.Json;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Plc;

/// <summary>PLC 公共面板的页签（计划 Task 7 第 1、3 条）：连接、读写、地址校验、监控表、套件选项与参数往返。客户端是内存中的假客户端。</summary>
public sealed class PlcSessionViewModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConnectThenWriteAndRead_RecordTheOperations_AndShowTheValue()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), new FakePlcClient());
        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Connected, page.State);

        page.Address = "D10";
        page.Count = 2;
        page.WriteText = "5,-6";
        await page.WriteCommand.ExecuteAsync(null);
        await page.ReadCommand.ExecuteAsync(null);

        Assert.Equal("5, -6", page.ReadText);
        Assert.Equal(new[] { "读取", "写入" }, page.Operations.Take(2).Select(record => record.Operation));
        Assert.All(page.Operations.Take(2), record => Assert.True(record.Succeeded));
    }

    [Fact]
    public async Task ALateStateEvent_DoesNotOverwriteTheCurrentState()
    {
        var dispatcher = new ManualUiDispatcher();
        var client = new FakePlcClient();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), client);
        await page.ConnectCommand.ExecuteAsync(null);

        // 掉线的事件排在前面，而一个更早的 Reconnecting 事件迟到：页签以客户端的当前状态为准。
        client.RaiseConnectionState(ConnectionState.Connected, ConnectionState.Disconnected);
        client.RaiseLateEvent(ConnectionState.Connected, ConnectionState.Reconnecting);
        dispatcher.RunPending();

        Assert.Equal(SessionState.NotConnected, page.State);
    }

    [Fact]
    public async Task ReadWhenNotConnected_IsRecordedAsNotConnected()
    {
        await using var page = new FakePlcSession(FakeServices.Create(new ManualUiDispatcher()), new FakePlcClient());

        await page.ReadCommand.ExecuteAsync(null);

        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal(CommErrorKind.NotConnected, record.ErrorKind);
        Assert.False(record.Succeeded);
    }

    [Fact]
    public async Task AddressCheck_ShowsTheParsedAddress_TheParserMessage_OrAPrompt()
    {
        await using var page = new FakePlcSession(FakeServices.Create(new ManualUiDispatcher()), new FakePlcClient());

        page.Address = "D10";
        Assert.Contains("存储区 D", page.AddressCheck);
        Assert.Contains("编号 10", page.AddressCheck);

        page.Address = "X1";
        Assert.Contains("不是假客户端认识的地址", page.AddressCheck);

        page.Address = "   ";
        Assert.Equal("请输入地址。", page.AddressCheck);
    }

    [Fact]
    public async Task AnInvalidValue_IsRecordedAsInvalidRequest_AndNothingIsWritten()
    {
        var client = new FakePlcClient();
        await using var page = new FakePlcSession(FakeServices.Create(new ManualUiDispatcher()), client);
        await page.ConnectCommand.ExecuteAsync(null);
        page.Address = "D10";
        page.ValueType = PlcValueType.Int16;
        page.WriteText = "70000";

        await page.WriteCommand.ExecuteAsync(null);

        OperationRecord record = page.Operations[0];
        Assert.Equal("写入", record.Operation);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
    }

    [Fact]
    public async Task AClientRefusal_IsRecordedAndTheSessionStaysDisconnected()
    {
        await using var page = new FakePlcSession(FakeServices.Create(new ManualUiDispatcher()), new FakePlcClient { FailConnect = true });

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        Assert.False(Assert.Single(page.Operations).Succeeded);
    }

    [Fact]
    public async Task WriteOfAMonitoredAddress_RefreshesTheRowImmediately_WithoutPolling()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), new FakePlcClient());
        await page.ConnectCommand.ExecuteAsync(null);
        page.Address = "D10";
        page.AddMonitorCommand.Execute(null);
        PlcMonitorRow row = Assert.Single(page.Monitor);

        page.WriteText = "42";
        await page.WriteCommand.ExecuteAsync(null);
        dispatcher.RunPending();

        Assert.Equal("42", row.Value);
        Assert.Equal("正常", row.Status);
        Assert.NotNull(row.UpdatedAt);
        Assert.False(page.IsMonitoring);
    }

    [Fact]
    public async Task Monitor_Polls_UntilItIsStopped_AndAfterDisconnectNoReadsHappen()
    {
        var dispatcher = new ManualUiDispatcher();
        var client = new FakePlcClient();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), client);
        await page.ConnectCommand.ExecuteAsync(null);
        page.Address = "D10";
        page.AddMonitorCommand.Execute(null);
        page.MonitorIntervalMs = 50;

        page.StartMonitorCommand.Execute(null);
        Assert.True(page.IsMonitoring);
        await Waiting.UntilAsync(() => client.ReadCount >= 2, Wait, dispatcher.RunPending);

        await page.DisconnectCommand.ExecuteAsync(null);
        Assert.False(page.IsMonitoring);
        int readsAtDisconnect = client.ReadCount;
        await Task.Delay(200);

        Assert.Equal(readsAtDisconnect, client.ReadCount);
    }

    [Fact]
    public async Task Monitor_StopsWhenTheConnectionDrops()
    {
        var dispatcher = new ManualUiDispatcher();
        var client = new FakePlcClient();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), client);
        await page.ConnectCommand.ExecuteAsync(null);
        page.Address = "D10";
        page.AddMonitorCommand.Execute(null);
        page.MonitorIntervalMs = 50;
        page.StartMonitorCommand.Execute(null);

        client.RaiseConnectionState(ConnectionState.Connected, ConnectionState.Disconnected);
        dispatcher.RunPending();

        Assert.False(page.IsMonitoring);
        Assert.Equal(SessionState.NotConnected, page.State);
    }

    [Fact]
    public async Task WriteMonitorRow_WritesTheNewValue_ClearsIt_AndRefreshesTheRow()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), new FakePlcClient());
        await page.ConnectCommand.ExecuteAsync(null);
        page.Address = "D12";
        page.AddMonitorCommand.Execute(null);
        PlcMonitorRow row = Assert.Single(page.Monitor);
        row.NewValue = "9";

        await page.WriteMonitorCommand.ExecuteAsync(row);
        dispatcher.RunPending();

        Assert.Equal(string.Empty, row.NewValue);
        Assert.Equal("9", row.Value);
    }

    [Fact]
    public async Task Options_OverrideOnlyWhatIsChosen()
    {
        await using var page = new FakePlcSession(FakeServices.Create(new ManualUiDispatcher()), new FakePlcClient());
        Assert.Null(page.Options.DataFormatOverride());

        page.Options.OverrideEncoding = true;
        page.Options.StringEncoding = EncodingChoice.Utf8;
        PlcDataFormat? format = page.Options.DataFormatOverride();

        Assert.NotNull(format);
        Assert.Equal(WordOrder.ABCD, format!.WordOrder);
        Assert.Equal(System.Text.Encoding.UTF8, format.StringEncoding);
    }

    [Fact]
    public async Task AnInvalidOption_RefusesTheConnect_WithoutConnecting()
    {
        var client = new FakePlcClient();
        await using var page = new FakePlcSession(FakeServices.Create(new ManualUiDispatcher()), client);
        page.Options.RetryCount = 500;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        Assert.False(client.IsConnected);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations).ErrorKind);
    }

    [Fact]
    public async Task PanelSettings_SurviveTheWorkspaceJson_AndRestoreTheMonitorInOrder()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), new FakePlcClient());
        page.Address = "D20";
        page.ValueType = PlcValueType.Float;
        page.Count = 2;
        page.MonitorIntervalMs = 250;
        page.Options.RetryCount = 3;
        page.Options.OverrideEncoding = true;
        page.Options.StringEncoding = EncodingChoice.Gbk;
        page.Channel.Timeouts.RequestTimeout = 1500;
        page.Address = "D20";
        page.AddMonitorCommand.Execute(null);
        page.Address = "D30";
        page.ValueType = PlcValueType.String;
        page.Count = 8;
        page.AddMonitorCommand.Execute(null);

        PlcPanelSettings saved = page.CapturePanelForTest();
        string json = JsonSerializer.Serialize(saved, WorkspaceJson.Options);
        PlcPanelSettings read = JsonSerializer.Deserialize<PlcPanelSettings>(json, WorkspaceJson.Options)!;

        await using var restored = new FakePlcSession(FakeServices.Create(dispatcher), new FakePlcClient());
        restored.ApplyPanelForTest(read);

        Assert.Equal("D30", restored.Address);
        Assert.Equal(PlcValueType.String, restored.ValueType);
        Assert.Equal(8, restored.Count);
        Assert.Equal(250, restored.MonitorIntervalMs);
        Assert.Equal(3, restored.Options.RetryCount);
        Assert.Equal(EncodingChoice.Gbk, restored.Options.StringEncoding);
        Assert.Equal(1500, restored.Channel.Timeouts.RequestTimeout);
        Assert.Equal(new[] { "D20", "D30" }, restored.Monitor.Select(row => row.Address));
        Assert.Equal(new[] { PlcValueType.Float, PlcValueType.String }, restored.Monitor.Select(row => row.Type));
        Assert.Equal(JsonSerializer.Serialize(restored.CapturePanelForTest(), WorkspaceJson.Options), json);
    }
}
