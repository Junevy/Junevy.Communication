using System.Text.Json;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Fins;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Fins;

/// <summary>
/// FINS 页签（计划 Task 10）：节点参数的范围检查（255 在连接前被拒绝），连接参数传给库，地址说明（十进制编号与位号），
/// 设置在工作区 JSON 中往返。
/// </summary>
public sealed class FinsSessionViewModelTests
{
    private static FinsSessionViewModel CreatePage(ManualUiDispatcher dispatcher, ScriptedTransports transports) =>
        new(ProtocolCatalog.Get(ProtocolKind.Fins), FakeServices.Create(dispatcher, transports: transports));

    [Fact]
    public async Task ClientNode255_IsRefusedOnConnect_WithTheBroadcastMessage()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.ClientNode = 255;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
        Assert.Contains("255", record.Message);
        Assert.Equal(0, transports.Tcp.CreateCount);
    }

    [Fact]
    public async Task ConnectionParameters_ReachTheTcpConfig()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Host = "10.4.5.6";
        page.Port = 9601;

        // 地址变化时会创建一个不连接的临时客户端，借此检查传给库的配置。
        page.Address = "D100";

        var config = Assert.IsType<TcpClientChannelConfig>(transports.Tcp.Config);
        Assert.Equal(("10.4.5.6", 9601), (config.Host, config.Port));
    }

    [Fact]
    public async Task Address_UsesDecimalNumberAndBit()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());

        page.Address = "W10.03";

        Assert.Equal("存储区 W，单位 Word，编号 10，位 3。", page.AddressCheck);
    }

    [Fact]
    public async Task Settings_SurviveTheWorkspaceJson_AndRestoreThePage()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher, new ScriptedTransports());
        page.Host = "10.4.5.6";
        page.Port = 9601;
        page.ClientNode = 12;
        page.DestinationNetwork = 2;
        page.DestinationUnit = 3;
        page.Address = "CIO0.0";

        string json = JsonSerializer.Serialize<SessionSettings>(page.CaptureSettings(), WorkspaceJson.Options);
        await using var restored = CreatePage(dispatcher, new ScriptedTransports());
        restored.ApplySettings(JsonSerializer.Deserialize<SessionSettings>(json, WorkspaceJson.Options)!);

        Assert.Equal(("10.4.5.6", 9601, 12, 2, 3), (restored.Host, restored.Port, restored.ClientNode, restored.DestinationNetwork, restored.DestinationUnit));
        Assert.Equal("CIO0.0", restored.Address);
        Assert.Equal(json, JsonSerializer.Serialize<SessionSettings>(restored.CaptureSettings(), WorkspaceJson.Options));
    }
}
