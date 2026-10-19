using System.Text.Json;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Siemens;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Siemens;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Siemens;

/// <summary>
/// S7 页签（计划 Task 10）：远端 TSAP 的显示与参数检查，连接参数传给库，字符串长度在发送前检查，地址说明（数据块与字节），
/// 设置在工作区 JSON 中往返。
/// </summary>
public sealed class S7SessionViewModelTests
{
    private static S7SessionViewModel CreatePage(ManualUiDispatcher dispatcher, ScriptedTransports transports) =>
        new(ProtocolCatalog.Get(ProtocolKind.S7), FakeServices.Create(dispatcher, transports: transports));

    [Fact]
    public async Task RemoteTsap_IsShownFromRackSlotAndConnectionType()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        Assert.Equal("0x0101（本地 0x0100）", page.RemoteTsapText);

        page.ConnectionType = S7ConnectionType.Basic;
        page.Rack = 2;
        page.Slot = 5;
        Assert.Equal("0x0345（本地 0x0100）", page.RemoteTsapText);

        page.Slot = 32;
        Assert.Contains("0 到 31", page.RemoteTsapText);
    }

    [Fact]
    public async Task InvalidRack_IsRefusedOnConnect_WithoutCreatingAChannel()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Rack = 8;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations).ErrorKind);
        Assert.Equal(0, transports.Tcp.CreateCount);
    }

    [Fact]
    public async Task ConnectionParameters_ReachTheTcpConfig()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Host = "10.1.2.3";
        page.Port = 1102;

        // 地址变化时会创建一个不连接的临时客户端，借此检查传给库的配置。
        page.Address = "DB1.DBB0";

        var config = Assert.IsType<TcpClientChannelConfig>(transports.Tcp.Config);
        Assert.Equal(("10.1.2.3", 1102), (config.Host, config.Port));
    }

    [Fact]
    public async Task Address_IsCheckedWithTheLibrary_AndNamesTheDataBlockAndByte()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());

        page.Address = "DB1.DBB12";

        Assert.Equal("DB1 的字节 12。", page.AddressCheck);
    }

    [Fact]
    public async Task AnAddressWithTheWrongWidthLetter_IsRejectedByTheLibrary()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());

        page.Address = "MX0.0";

        Assert.Contains("MX0.0", page.AddressCheck);
        Assert.DoesNotContain("的字节", page.AddressCheck);
    }

    [Theory]
    [InlineData(S7StringKind.String, 255)]
    [InlineData(S7StringKind.WString, 16383)]
    public async Task StringLength_OutOfRange_IsRecordedWithoutSending(S7StringKind kind, int maxLength)
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.StringKind = kind;
        page.StringMaxLength = maxLength;

        await page.ReadStringCommand.ExecuteAsync(null);
        await page.WriteStringCommand.ExecuteAsync(null);

        Assert.Equal(2, page.Operations.Count(record => record.ErrorKind == CommErrorKind.InvalidRequest));
        Assert.Equal(0, transports.Tcp.CreateCount);
    }

    [Fact]
    public async Task StringCommands_WithoutAConnection_AreRecorded()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());

        await page.ReadStringCommand.ExecuteAsync(null);

        Assert.Equal(CommErrorKind.NotConnected, Assert.Single(page.Operations).ErrorKind);
    }

    [Fact]
    public async Task Settings_SurviveTheWorkspaceJson_AndRestoreThePage()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher, new ScriptedTransports());
        page.Host = "10.0.0.5";
        page.Port = 1102;
        page.Rack = 2;
        page.Slot = 3;
        page.ConnectionType = S7ConnectionType.OP;
        page.StringKind = S7StringKind.WString;
        page.StringMaxLength = 120;
        page.StringAddress = "DB5.DBB0";
        page.StringValue = "配方";
        page.Address = "DB1.DBW12";

        string json = JsonSerializer.Serialize<SessionSettings>(page.CaptureSettings(), WorkspaceJson.Options);
        await using var restored = CreatePage(dispatcher, new ScriptedTransports());
        restored.ApplySettings(JsonSerializer.Deserialize<SessionSettings>(json, WorkspaceJson.Options)!);

        Assert.Equal(("10.0.0.5", 1102, 2, 3, S7ConnectionType.OP), (restored.Host, restored.Port, restored.Rack, restored.Slot, restored.ConnectionType));
        Assert.Equal((S7StringKind.WString, 120, "DB5.DBB0", "配方"), (restored.StringKind, restored.StringMaxLength, restored.StringAddress, restored.StringValue));
        Assert.Equal("DB1.DBW12", restored.Address);
        Assert.Equal(json, JsonSerializer.Serialize<SessionSettings>(restored.CaptureSettings(), WorkspaceJson.Options));
    }
}
