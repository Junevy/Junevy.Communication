using Junevy.Communication.Secs;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// SML 文本（计划第 17.3 节）：两个样例逐字比较，加上转义、空项、嵌套缩进与浮点格式。
/// SML 只用于日志与调试，库不做解析，因此这里只锁住输出。
/// </summary>
public sealed class SecsSmlTests
{
    [Fact]
    public void V_H_11_RendersTheSampledText()
    {
        SecsMessage message = SecsMessage.Create(
            1,
            2,
            replyExpected: false,
            body: SecsItem.L(SecsItem.A("EQ01"), SecsItem.A("1.0.0")));

        Assert.Equal(
            "S1F2\n<L [2]\n  <A \"EQ01\">\n  <A \"1.0.0\">\n>\n.",
            message.ToSml());
    }

    [Fact]
    public void V_H_12_RendersTheSampledText()
    {
        SecsMessage message = SecsMessage.Create(
            5,
            1,
            replyExpected: true,
            body: SecsItem.L(SecsItem.B(0x80), SecsItem.U4(1001), SecsItem.A("OVERHEAT")));

        Assert.Equal(
            "S5F1 W\n<L [3]\n  <B 0x80>\n  <U4 1001>\n  <A \"OVERHEAT\">\n>\n.",
            message.ToSml());
    }

    [Fact]
    public void AMessageWithoutABody_IsTwoLines()
    {
        Assert.Equal("S1F1 W\n.", SecsMessage.Create(1, 1, replyExpected: true).ToSml());
        Assert.Equal("S1F1\n.", SecsMessage.Create(1, 1, replyExpected: false).ToSml());
    }

    [Fact]
    public void ToString_MatchesToSml()
    {
        SecsMessage message = SecsMessage.Create(9, 9, replyExpected: false, body: SecsItem.U4(1));

        Assert.Equal(message.ToSml(), message.ToString());
    }

    [Fact]
    public void AnItemOnItsOwn_RendersTheBodyFragmentWithoutTheMessageHeader()
    {
        Assert.Equal("<U4 1>", SecsItem.U4(1).ToSml());
        Assert.Equal("<U4 1>", SecsItem.U4(1).ToString());
    }

    [Fact]
    public void NestedLists_IndentTwoSpacesPerLevel()
    {
        SecsMessage message = SecsMessage.Create(
            2,
            41,
            replyExpected: true,
            body: SecsItem.L(SecsItem.L(SecsItem.U1(1)), SecsItem.A("x")));

        Assert.Equal(
            "S2F41 W\n<L [2]\n  <L [1]\n    <U1 1>\n  >\n  <A \"x\">\n>\n.",
            message.ToSml());
    }

    [Fact]
    public void ABodyItemStartsInTheFirstColumn()
    {
        Assert.Equal("S1F1 W\n<U4 1>\n.", SecsMessage.Create(1, 1, replyExpected: true, SecsItem.U4(1)).ToSml());
    }

    [Fact]
    public void EmptyItems_RenderTheTagAlone()
    {
        SecsMessage message = SecsMessage.Create(
            1,
            1,
            replyExpected: false,
            body: SecsItem.L(SecsItem.U4(), SecsItem.B(), SecsItem.Boolean(), SecsItem.L()));

        Assert.Equal("S1F1\n<L [4]\n  <U4>\n  <B>\n  <BOOLEAN>\n  <L [0]>\n>\n.", message.ToSml());
    }

    [Fact]
    public void Booleans_RenderAsTAndF()
    {
        SecsMessage message = SecsMessage.Create(1, 1, replyExpected: false, SecsItem.Boolean(true, false, true));

        Assert.Equal("S1F1\n<BOOLEAN T F T>\n.", message.ToSml());
    }

    [Fact]
    public void Numbers_RenderSpaceSeparatedWithTheInvariantCulture()
    {
        SecsMessage message = SecsMessage.Create(
            1,
            1,
            replyExpected: false,
            body: SecsItem.L(SecsItem.I2(-2), SecsItem.U8(1), SecsItem.F4(1.5f), SecsItem.F8(1.5)));

        Assert.Equal("S1F1\n<L [4]\n  <I2 -2>\n  <U8 1>\n  <F4 1.5>\n  <F8 1.5>\n>\n.", message.ToSml());
    }

    [Fact]
    public void Floats_RenderWithThePrecisionThatRoundTripsOnEveryTarget()
    {
        // 单精度用 G9、双精度用 G17：.NET Framework 上 "R" 不能保证往返（官方文档对 Single 给 G9、对 Double 给 G17），
        // 而 G9 / G17 在 net472 与 net8.0 上给出同样的文本，跨目标可复现。代价是 0.1 会写成 17 位有效数字。
        SecsMessage message = SecsMessage.Create(1, 1, replyExpected: false, body: SecsItem.L(SecsItem.F4(0.1f), SecsItem.F8(0.1)));

        Assert.Equal("S1F1\n<L [2]\n  <F4 0.100000001>\n  <F8 0.10000000000000001>\n>\n.", message.ToSml());
    }

    [Fact]
    public void Ascii_EscapesTheQuoteTheBackslashAndEverythingOutsidePrintableAscii()
    {
        SecsMessage message = SecsMessage.Create(1, 1, replyExpected: false, SecsItem.A("\"\\\x01\x7F\u00ff~\n"));

        Assert.Equal("S1F1\n<A \"\\\"\\\\\\x01\\x7F\\xFF~\\x0A\">\n.", message.ToSml());
    }
}