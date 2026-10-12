using Junevy.Communication.Omron.Protocol;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 会话状态（计划 Task 13 第 2 条、Q27、P4-D18）：握手前没有节点号，握手整体替换一个不可变快照；
/// 钩子读到的永远是完整一致的快照而不是半更新的字段。快照里保存的是握手的最终结果——一条可直接使用的路由。
/// </summary>
public sealed class FinsSessionTests
{
    [Fact]
    public void NewSession_HasNoRouteBecauseTheNodeHandshakeHasNotRun()
    {
        Assert.Null(new FinsSession().Current.Route);
    }

    [Fact]
    public void ReplaceAfterHandshake_ExposesTheRoute()
    {
        var session = new FinsSession();

        session.ReplaceAfterHandshake(Route);

        FinsRoute? route = session.Current.Route;
        Assert.NotNull(route);
        Assert.Equal(ClientNode, route.Value.SourceNode);
        Assert.Equal(ServerNode, route.Value.DestinationNode);
    }

    [Fact]
    public void ReplaceAfterHandshake_ReplacesTheWholeSnapshot()
    {
        var session = new FinsSession();
        session.ReplaceAfterHandshake(Route);
        FinsSessionState first = session.Current;

        session.ReplaceAfterHandshake(new FinsRoute(0, 0x02, 0, 0, 0x0C, 0));

        // 重连后节点号可能变化：旧快照保持不变，读取方要么拿到旧的一致值、要么拿到新的一致值，不会混在一起。
        Assert.NotSame(first, session.Current);
        Assert.Equal(ClientNode, first.Route!.Value.SourceNode);
        Assert.Equal(0x0C, session.Current.Route!.Value.SourceNode);
        Assert.Equal(0x02, session.Current.Route!.Value.DestinationNode);
    }

    [Fact]
    public void Current_IsTheSameSnapshotUntilTheNextHandshake()
    {
        var session = new FinsSession();

        Assert.Same(session.Current, session.Current);
        Assert.Same(FinsSessionState.BeforeHandshake, session.Current);
    }

    [Fact]
    public void AfterHandshake_KeepsEveryRouteByteItWasGiven()
    {
        var route = new FinsRoute(destinationNetwork: 3, destinationNode: 4, destinationUnit: 5,
                                  sourceNetwork: 6, sourceNode: 7, sourceUnit: 8);

        FinsRoute stored = FinsSessionState.AfterHandshake(route).Route!.Value;

        Assert.Equal(3, stored.DestinationNetwork);
        Assert.Equal(4, stored.DestinationNode);
        Assert.Equal(5, stored.DestinationUnit);
        Assert.Equal(6, stored.SourceNetwork);
        Assert.Equal(7, stored.SourceNode);
        Assert.Equal(8, stored.SourceUnit);
    }
}
