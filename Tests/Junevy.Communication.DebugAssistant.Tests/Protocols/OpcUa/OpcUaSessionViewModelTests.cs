using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.OpcUa;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.OpcUa;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.OpcUa;

/// <summary>
/// OPC UA 页的视图模型，不连接服务器（计划 Task 17 第 2 条的单元部分）：默认值、设置往返、端点为空时拒绝、未连接时命令不可用、
/// 读写列表的增删、浏览树只允许变量加入列表、懒加载节点的形状。网络行为见 <c>OpcUaEndToEndTests</c>。
/// </summary>
public sealed class OpcUaSessionViewModelTests
{
    private static OpcUaSessionViewModel CreatePage() =>
        new(ProtocolCatalog.Get(ProtocolKind.OpcUa), FakeServices.Create(new ManualUiDispatcher()));

    [Fact]
    public async Task Page_StartsNotConnected_WithAllFifteenWriteTypes()
    {
        await using var page = CreatePage();

        Assert.Equal(("未连接", SessionState.NotConnected, false), (page.OpcUaStateText, page.State, page.IsLinked));
        Assert.Equal(15, page.WriteTypeChoices.Count);
        Assert.True(page.ConnectCommand.CanExecute(null));
        Assert.False(page.ReadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        await using var page = CreatePage();
        page.EndpointUrl = "opc.tcp://plc.example:4840/UA";
        page.ApplicationUri = "urn:junevy:debug";
        page.PkiRoot = @"C:\pki";
        page.UserName = "operator";
        page.Password = "secret";
        page.ConnectTimeout = 3000;
        page.OperationTimeout = 4000;
        page.SessionTimeout = 90000;
        page.KeepAliveInterval = 2000;
        page.ReconnectInterval = 500;
        page.MaxReconnectInterval = 8000;
        page.PublishingInterval = 250;
        page.SamplingInterval = 100;
        page.QueueSize = 5;
        page.DiscardOldest = false;

        var saved = (OpcUaSettings)page.CaptureSettings();
        await using var other = CreatePage();
        other.ApplySettings(saved);

        Assert.Equal(saved, other.CaptureSettings());
        Assert.Equal(("operator", "secret"), (saved.UserName, saved.Password));
    }

    [Fact]
    public async Task BlankCredentials_AreSavedAsAnonymous()
    {
        await using var page = CreatePage();
        page.UserName = "  ";

        var saved = (OpcUaSettings)page.CaptureSettings();

        Assert.Equal((null, null), (saved.UserName, saved.Password));
    }

    [Fact]
    public async Task APasswordWithoutAUserName_IsRefusedWithInvalidRequest()
    {
        await using var page = CreatePage();
        page.Password = "secret";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal((false, CommErrorKind.InvalidRequest), (record.Succeeded, record.ErrorKind));
    }

    [Fact]
    public async Task ConnectWithoutAnEndpoint_IsRefusedWithInvalidRequest_AndRecorded()
    {
        await using var page = CreatePage();
        page.EndpointUrl = " ";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal(("连接", false, CommErrorKind.InvalidRequest), (record.Operation, record.Succeeded, record.ErrorKind));
        Assert.Equal("端点地址不能为空。", record.Message);
    }

    [Fact]
    public async Task BrowseReadWriteAndSubscribe_AreUnavailable_UntilConnected()
    {
        await using var page = CreatePage();

        Assert.Equal(
            (false, false, false, false),
            (page.BrowseRootCommand.CanExecute(null), page.ReadCommand.CanExecute(null), page.WriteCommand.CanExecute(null), page.SubscribeCommand.CanExecute(null)));
    }

    [Fact]
    public async Task AddNode_AddsARow_ClearsTheInput_AndHintsAtNamespaceIndexes()
    {
        await using var page = CreatePage();
        page.NewNodeId = " ns=2;s=Line1.Speed ";

        page.AddNodeCommand.Execute(null);

        OpcUaReadRow row = Assert.Single(page.Items);
        Assert.Equal(("ns=2;s=Line1.Speed", string.Empty), (row.NodeId, page.NewNodeId));
        Assert.NotEmpty(row.Hint);
    }

    [Fact]
    public async Task AddNode_RefusesAnEmptyNodeId()
    {
        await using var page = CreatePage();
        page.NewNodeId = "   ";

        page.AddNodeCommand.Execute(null);

        Assert.Empty(page.Items);
        Assert.Equal("NodeId 不能为空。", page.ListMessage);
    }

    [Fact]
    public async Task RemoveNode_RemovesTheRow()
    {
        await using var page = CreatePage();
        page.NewNodeId = "nsu=urn:junevy:opcua:test;s=Counter";
        page.AddNodeCommand.Execute(null);

        page.RemoveNodeCommand.Execute(page.Items[0]);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task DoubleClickingAVariable_AddsIt_ButAnObjectIsRefused()
    {
        await using var page = CreatePage();
        var variable = new OpcUaNodeItem("ns=2;i=5", "Counter", "Counter", OpcUaNodeClass.Variable, _ => Task.CompletedTask);
        var folder = new OpcUaNodeItem("ns=2;i=4", "Test", "Test", OpcUaNodeClass.Object, _ => Task.CompletedTask);

        page.AddBrowsedNodeCommand.Execute(folder);
        Assert.Empty(page.Items);
        Assert.Contains("不是变量", page.BrowseMessage);

        page.AddBrowsedNodeCommand.Execute(variable);
        Assert.Equal("ns=2;i=5", Assert.Single(page.Items).NodeId);
    }

    [Fact]
    public void Objects_HaveAPlaceholderChild_UntilTheyAreBrowsed_AndVariablesAreLeaves()
    {
        var folder = new OpcUaNodeItem("ns=2;i=4", "Test", "Test", OpcUaNodeClass.Object, _ => Task.CompletedTask);
        var variable = new OpcUaNodeItem("ns=2;i=5", "Counter", "Counter", OpcUaNodeClass.Variable, _ => Task.CompletedTask);

        Assert.Equal((true, false, true), (folder.CanHaveChildren, folder.IsLoaded, folder.Children[0].IsPlaceholder));
        Assert.Empty(variable.Children);
        Assert.True(variable.IsVariable);
    }
}
