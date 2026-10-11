namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 会话状态（计划第 12 节第 2 条、P4-D9、P4-D12、Q27）：握手前没有协商结果，握手整体替换一个不可变快照，
/// 钩子读到的永远是完整一致的快照而不是半更新字段。分包上限由协商出的 PDU 长度推导。
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

    [Theory]
    [InlineData(240, 222, 212, 111, 106)]
    [InlineData(960, 942, 932, 471, 466)]
    [InlineData(64, 46, 36, 23, 18)]
    public void AfterHandshake_DerivesThePacketLimitsFromTheNegotiatedPdu(int pdu, int readBytes, int writeBytes, int readWords, int writeWords)
    {
        // P4-D12：读包头 18 字节、写包头 28 字节；字数按字节数除以 2 向下取整（末尾的半个字节读不到也不写）。
        S7SessionState state = S7SessionState.AfterHandshake(pdu);

        Assert.Equal(readBytes, state.MaxReadBytes);
        Assert.Equal(writeBytes, state.MaxWriteBytes);
        Assert.Equal(readWords, state.MaxReadWords);
        Assert.Equal(writeWords, state.MaxWriteWords);
    }

    [Fact]
    public void BeforeHandshake_HasNoPacketLimits()
    {
        S7SessionState state = S7SessionState.BeforeHandshake;

        Assert.Equal(0, state.MaxReadBytes);
        Assert.Equal(0, state.MaxWriteBytes);
        Assert.Equal(0, state.MaxReadWords);
        Assert.Equal(0, state.MaxWriteWords);
    }
}
