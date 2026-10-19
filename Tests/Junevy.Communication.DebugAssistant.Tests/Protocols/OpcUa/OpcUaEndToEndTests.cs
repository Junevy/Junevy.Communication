using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.OpcUa;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.OpcUa;
using Junevy.Communication.Testing.Servers;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.OpcUa;

/// <summary>
/// 端到端用例共用的 OPC UA 服务器集合。定义必须与用例在同一个测试程序集里，xUnit 才能发现它；服务器本身来自 Testing.Servers（Task 16）。
/// </summary>
[CollectionDefinition(Name)]
public sealed class DebugAssistantOpcUaServerCollection : ICollectionFixture<OpcUaServerFixture>
{
    /// <summary>集合名。</summary>
    public const string Name = "DebugAssistant.OpcUaServer";
}

/// <summary>
/// OPC UA 页与进程内服务器（Task 16 的共享 <see cref="OpcUaTestServer"/>）的端到端测试（计划 Task 17 第 2 条）：浏览 <c>i=85</c> 并懒加载展开；
/// 批量读中混入未知节点，逐项状态；每种可写类型各写一次（类型选择器的转换）；类型不匹配的 BadTypeMismatch；输入无法解析时不发送任何写入；
/// 订阅收到初始值，释放订阅后不再收到通知；拒绝匿名的服务器返回不支持。集合内串行，用例可以读写同一批变量。
/// </summary>
[Collection(DebugAssistantOpcUaServerCollection.Name)]
public sealed class OpcUaEndToEndTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly OpcUaServerFixture _fixture;

    public OpcUaEndToEndTests(OpcUaServerFixture fixture)
    {
        _fixture = fixture;
    }

    private OpcUaSessionViewModel CreatePage(ManualUiDispatcher dispatcher)
    {
        var page = new OpcUaSessionViewModel(ProtocolCatalog.Get(ProtocolKind.OpcUa), FakeServices.Create(dispatcher));
        page.EndpointUrl = _fixture.Server.EndpointUrl;
        page.PkiRoot = OpcUaTestServerFactory.UniquePkiRoot();
        return page;
    }

    // 沿着 DisplayName 路径逐层展开（懒加载）。第一层从已经浏览过的 i=85 开始。
    private static async Task<OpcUaNodeItem> ExpandPathAsync(OpcUaSessionViewModel page, params string[] path)
    {
        OpcUaNodeItem node = page.BrowseRoots[0];
        foreach (string name in path)
        {
            OpcUaNodeItem child = node.Children.Single(candidate => candidate.DisplayName == name);
            if (!child.IsLoaded)
            {
                child.IsExpanded = true;
                await Waiting.UntilAsync(() => child.IsLoaded, Wait);
            }

            node = child;
        }

        return node;
    }

    [Fact]
    public async Task Browse_I85_ShowsTheFolders_AndExpandsLazily()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher);
        await page.ConnectCommand.ExecuteAsync(null);

        await page.BrowseRootCommand.ExecuteAsync(null);

        OpcUaNodeItem root = Assert.Single(page.BrowseRoots);
        Assert.Equal(("i=85", "Objects", true), (root.NodeId, root.DisplayName, root.IsLoaded));
        OpcUaNodeItem testFolder = root.Children.Single(child => child.DisplayName == "Test");
        Assert.Equal((OpcUaNodeClass.Object, true, false), (testFolder.NodeClass, testFolder.CanHaveChildren, testFolder.IsLoaded));
        Assert.StartsWith("ns=", testFolder.NodeId);

        OpcUaNodeItem expanded = await ExpandPathAsync(page, "Test");
        Assert.True(expanded.IsLoaded);
        OpcUaNodeItem counter = expanded.Children.Single(child => child.DisplayName == "Counter");
        Assert.Equal((OpcUaNodeClass.Variable, false), (counter.NodeClass, counter.CanHaveChildren));
        Assert.StartsWith("ns=", counter.NodeId);

        page.AddBrowsedNodeCommand.Execute(counter);
        Assert.Equal(counter.NodeId, Assert.Single(page.Items).NodeId);
    }

    [Fact]
    public async Task BatchRead_ReportsEachItemsStatus_WithAnUnknownNodeMixedIn()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher);
        await page.ConnectCommand.ExecuteAsync(null);
        await page.BrowseRootCommand.ExecuteAsync(null);
        OpcUaNodeItem counter = (await ExpandPathAsync(page, "Test")).Children.Single(child => child.DisplayName == "Counter");
        page.AddBrowsedNodeCommand.Execute(counter);
        page.NewNodeId = "nsu=urn:junevy:opcua:test;s=Missing";
        page.AddNodeCommand.Execute(null);

        await page.ReadCommand.ExecuteAsync(null);

        OpcUaReadRow known = page.Items[0];
        OpcUaReadRow unknown = page.Items[1];
        Assert.StartsWith("好：", known.Status);
        Assert.Equal("int", known.DotNetType);
        Assert.True(int.TryParse(known.Value, out _));
        Assert.Equal("坏：BadNodeIdUnknown (0x80340000)", unknown.Status);
        Assert.Equal("已读取 2 项，逐项结果见读写列表。", page.ListMessage);
    }

    [Fact]
    public async Task EveryWritableType_IsWrittenOnce_AndReadBackAsTheNamedType()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher);
        await page.ConnectCommand.ExecuteAsync(null);
        await page.BrowseRootCommand.ExecuteAsync(null);
        OpcUaNodeItem writable = await ExpandPathAsync(page, "Test", "Writable");
        foreach (OpcUaNodeItem variable in writable.Children)
        {
            page.AddBrowsedNodeCommand.Execute(variable);
        }

        // 节点名称 → (写入类型, 是否数组, 输入文字, 读回的期望文字)：15 种标量（ByteString 即 byte[]）与 13 个一维数组。
        var cases = new Dictionary<string, (OpcUaWriteType Type, bool IsArray, string Text, string Expected)>
        {
            ["Bool"] = (OpcUaWriteType.Boolean, false, "true", "True"),
            ["SByte"] = (OpcUaWriteType.SByte, false, "-7", "-7"),
            ["Byte"] = (OpcUaWriteType.Byte, false, "7", "7"),
            ["Int16"] = (OpcUaWriteType.Int16, false, "-300", "-300"),
            ["UInt16"] = (OpcUaWriteType.UInt16, false, "300", "300"),
            ["Int32"] = (OpcUaWriteType.Int32, false, "-70000", "-70000"),
            ["UInt32"] = (OpcUaWriteType.UInt32, false, "70000", "70000"),
            ["Int64"] = (OpcUaWriteType.Int64, false, "-5000000000", "-5000000000"),
            ["UInt64"] = (OpcUaWriteType.UInt64, false, "5000000000", "5000000000"),
            ["Float"] = (OpcUaWriteType.Float, false, "1.25", "1.25"),
            ["Double"] = (OpcUaWriteType.Double, false, "2.5", "2.5"),
            ["String"] = (OpcUaWriteType.String, false, "hi there", "hi there"),
            ["DateTime"] = (OpcUaWriteType.DateTime, false, "2026-01-02T03:04:05Z", "2026-01-02 03:04:05.000 UTC"),
            ["Guid"] = (OpcUaWriteType.Guid, false, "11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555"),
            ["ByteString"] = (OpcUaWriteType.ByteString, false, "hex: 01 FF 10", "01 FF 10"),
            ["BoolArray"] = (OpcUaWriteType.Boolean, true, "true\nfalse", "True, False"),
            ["SByteArray"] = (OpcUaWriteType.SByte, true, "-1\n-2", "-1, -2"),
            ["Int16Array"] = (OpcUaWriteType.Int16, true, "-3\n-4", "-3, -4"),
            ["UInt16Array"] = (OpcUaWriteType.UInt16, true, "3\n4", "3, 4"),
            ["Int32Array"] = (OpcUaWriteType.Int32, true, "-5\n-6", "-5, -6"),
            ["UInt32Array"] = (OpcUaWriteType.UInt32, true, "5\n6", "5, 6"),
            ["Int64Array"] = (OpcUaWriteType.Int64, true, "-7\n-8", "-7, -8"),
            ["UInt64Array"] = (OpcUaWriteType.UInt64, true, "7\n8", "7, 8"),
            ["FloatArray"] = (OpcUaWriteType.Float, true, "-1.5\n-2.5", "-1.5, -2.5"),
            ["DoubleArray"] = (OpcUaWriteType.Double, true, "-3.5\n-4.5", "-3.5, -4.5"),
            ["StringArray"] = (OpcUaWriteType.String, true, "x\ny", "x, y"),
            ["DateTimeArray"] = (OpcUaWriteType.DateTime, true, "2026-05-06T07:08:09Z", "2026-05-06 07:08:09.000 UTC"),
            ["GuidArray"] = (OpcUaWriteType.Guid, true, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        };
        Dictionary<string, string> names = writable.Children.ToDictionary(child => child.NodeId, child => child.DisplayName);
        Assert.Equal(cases.Count, page.Items.Count);

        foreach (OpcUaReadRow row in page.Items)
        {
            (OpcUaWriteType type, bool isArray, string text, _) = cases[names[row.NodeId]];
            row.WriteType = type;
            row.IsArray = isArray;
            row.WriteText = text;
        }

        await page.WriteCommand.ExecuteAsync(null);

        Assert.Equal($"已写入 {cases.Count} 项，逐项结果见写入结果。", page.ListMessage);
        Assert.All(page.Items, row => Assert.StartsWith("好：", row.WriteStatus));

        await page.ReadCommand.ExecuteAsync(null);
        foreach (OpcUaReadRow row in page.Items)
        {
            Assert.Equal(cases[names[row.NodeId]].Expected, row.Value);
        }
    }

    [Fact]
    public async Task AWrongType_IsAnsweredByTheServer_WithBadTypeMismatch_OnlyForThatItem()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher);
        await page.ConnectCommand.ExecuteAsync(null);
        await page.BrowseRootCommand.ExecuteAsync(null);
        OpcUaNodeItem writable = await ExpandPathAsync(page, "Test", "Writable");
        page.AddBrowsedNodeCommand.Execute(writable.Children.Single(child => child.DisplayName == "Int16"));
        page.AddBrowsedNodeCommand.Execute(writable.Children.Single(child => child.DisplayName == "Int32"));
        OpcUaReadRow int16 = page.Items[0];
        OpcUaReadRow int32 = page.Items[1];
        int16.WriteType = OpcUaWriteType.Int32;
        int16.WriteText = "5";
        int32.WriteType = OpcUaWriteType.Int32;
        int32.WriteText = "9";

        await page.WriteCommand.ExecuteAsync(null);

        Assert.StartsWith("坏：BadTypeMismatch", int16.WriteStatus);
        Assert.StartsWith("好：", int32.WriteStatus);
    }

    [Fact]
    public async Task AnUnparseableValue_SendsNothing_AndMarksItsRow()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher);
        await page.ConnectCommand.ExecuteAsync(null);
        await page.BrowseRootCommand.ExecuteAsync(null);
        OpcUaNodeItem writable = await ExpandPathAsync(page, "Test", "Writable");
        page.AddBrowsedNodeCommand.Execute(writable.Children.Single(child => child.DisplayName == "Int32"));
        page.AddBrowsedNodeCommand.Execute(writable.Children.Single(child => child.DisplayName == "Double"));
        OpcUaReadRow bad = page.Items[0];
        OpcUaReadRow good = page.Items[1];
        bad.WriteType = OpcUaWriteType.Int32;
        bad.WriteText = "abc";
        good.WriteType = OpcUaWriteType.Double;
        good.WriteText = "9.5";

        await page.WriteCommand.ExecuteAsync(null);

        Assert.Equal("有写入值无法按所选类型解析，未发送任何写入。", page.ListMessage);
        Assert.StartsWith("输入无效：无法解析为 Int32", bad.WriteStatus);
        Assert.Equal(string.Empty, good.WriteStatus);
    }

    [Fact]
    public async Task Subscription_DeliversTheInitialValue_AndNothingAfterRelease()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher);
        page.PublishingInterval = 100;
        await page.ConnectCommand.ExecuteAsync(null);
        await page.BrowseRootCommand.ExecuteAsync(null);
        OpcUaNodeItem counter = (await ExpandPathAsync(page, "Test")).Children.Single(child => child.DisplayName == "Counter");
        page.AddBrowsedNodeCommand.Execute(counter);

        await page.SubscribeCommand.ExecuteAsync(null);
        Assert.StartsWith("好：", Assert.Single(page.SubscribedItems).Status);
        await Waiting.UntilAsync(() => page.Notifications.Count >= 1, Wait, dispatcher.RunPending);
        Assert.Equal(counter.NodeId, page.Notifications[0].NodeId);

        _fixture.Server.SetCounter("Test/Counter", 5);
        await Waiting.UntilAsync(() => page.Notifications.Any(row => row.Value == "5"), Wait, dispatcher.RunPending);

        await page.ReleaseSubscriptionCommand.ExecuteAsync(null);
        Assert.Empty(page.SubscribedItems);
        int received = page.Notifications.Count;

        _fixture.Server.SetCounter("Test/Counter", 6);
        await Task.Delay(TimeSpan.FromSeconds(1));
        dispatcher.RunPending();
        Assert.Equal(received, page.Notifications.Count);
        Assert.DoesNotContain(page.Notifications, row => row.Value == "6");
    }

    [Fact]
    public async Task AServerThatRefusesAnonymous_IsNotSupported_AndNothingIsLeftConnected()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(options => options.AllowAnonymous = false);
        var dispatcher = new ManualUiDispatcher();
        var page = new OpcUaSessionViewModel(ProtocolCatalog.Get(ProtocolKind.OpcUa), FakeServices.Create(dispatcher))
        {
            EndpointUrl = server.EndpointUrl,
            PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
        };
        await using (page)
        {
            await page.ConnectCommand.ExecuteAsync(null);

            Assert.Equal(SessionState.NotConnected, page.State);
            OperationRecord record = Assert.Single(page.Operations);
            Assert.Equal((false, CommErrorKind.NotSupported), (record.Succeeded, record.ErrorKind));
        }
    }
}
