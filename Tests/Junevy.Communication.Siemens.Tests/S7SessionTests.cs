namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 会话状态（计划第 12 节第 2 条、P4-D9、Q27）：握手前没有协商结果，握手整体替换一个不可变快照，
/// 钩子读到的永远是完整一致的快照而不是半更新字段。
/// </summary>
public sealed class S7SessionTests
{
    [Fact]
    public void NewSession_HasNoNegotiatedPdu()
    {
        Assert.Null(new S7Session().Current.NegotiatedPduBytes);
    }

    [Fact]
    public void ReplaceAfterHandshake_ExposesTheNegotiatedPdu()
    {
        var session = new S7Session();

        session.ReplaceAfterHandshake(240);

        Assert.Equal(240, session.Current.NegotiatedPduBytes);
    }

    [Fact]
    public void ReplaceAfterHandshake_ReplacesTheWholeSnapshot()
    {
        var session = new S7Session();
        session.ReplaceAfterHandshake(240);
        S7SessionState first = session.Current;

        session.ReplaceAfterHandshake(960);

        // 重连后协商值变化：旧快照保持不变，读取方要么拿到旧的一致值、要么拿到新的一致值，不会混在一起。
        Assert.NotSame(first, session.Current);
        Assert.Equal(240, first.NegotiatedPduBytes);
        Assert.Equal(960, session.Current.NegotiatedPduBytes);
    }

    [Fact]
    public void Current_IsTheSameSnapshotUntilTheNextHandshake()
    {
        var session = new S7Session();

        Assert.Same(session.Current, session.Current);
        Assert.Same(S7SessionState.BeforeHandshake, session.Current);
    }
}
