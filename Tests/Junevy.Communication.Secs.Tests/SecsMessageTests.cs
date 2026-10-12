using Junevy.Communication.Secs;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// SECS-II 消息的创建规则（计划 Task 2）：Stream 与 Function 的取值、W 位与偶数 Function 的互斥、
/// <see cref="SecsMessage.CreateReply"/> 生成的应答，以及属性本身。
/// </summary>
public sealed class SecsMessageTests
{
    [Fact]
    public void Create_KeepsStreamFunctionWBitAndBody()
    {
        SecsItem body = SecsItem.U4(1);
        SecsMessage message = SecsMessage.Create(1, 13, replyExpected: true, body);

        Assert.Equal((byte)1, message.Stream);
        Assert.Equal((byte)13, message.Function);
        Assert.True(message.ReplyExpected);
        Assert.Same(body, message.Body);
    }

    [Fact]
    public void Create_WithoutABody_LeavesTheBodyNull()
    {
        Assert.Null(SecsMessage.Create(1, 1, replyExpected: false).Body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    public void Create_AcceptsTheWholeStreamRange(int stream)
    {
        Assert.Equal((byte)stream, SecsMessage.Create((byte)stream, 1, replyExpected: false).Stream);
    }

    [Fact]
    public void Create_RejectsAStreamAbove127()
    {
        ArgumentOutOfRangeException error =
            Assert.Throws<ArgumentOutOfRangeException>(() => SecsMessage.Create(128, 1, replyExpected: false));
        Assert.Equal("stream", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(254)]
    public void Create_RejectsAnEvenFunctionThatAsksForAReply(int function)
    {
        // 应答不能带 W 位：偶数 Function 本身就是应答。
        ArgumentException error =
            Assert.Throws<ArgumentException>(() => SecsMessage.Create(1, (byte)function, replyExpected: true));
        Assert.Contains("replyExpected", error.Message);
    }

    [Fact]
    public void Create_AcceptsAnEvenFunctionWithoutTheWBit()
    {
        SecsMessage message = SecsMessage.Create(1, 2, replyExpected: false);

        Assert.Equal((byte)2, message.Function);
        Assert.False(message.ReplyExpected);
    }

    [Fact]
    public void CreateReply_KeepsTheStreamAndIncrementsTheFunction()
    {
        SecsItem body = SecsItem.A("OK");
        SecsMessage reply = SecsMessage.Create(7, 41, replyExpected: true, SecsItem.U1(1)).CreateReply(body);

        Assert.Equal((byte)7, reply.Stream);
        Assert.Equal((byte)42, reply.Function);
        Assert.False(reply.ReplyExpected);
        Assert.Same(body, reply.Body);
    }

    [Fact]
    public void CreateReply_WithoutABody_LeavesTheBodyNull()
    {
        Assert.Null(SecsMessage.Create(1, 1, replyExpected: true).CreateReply().Body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(254)]
    public void CreateReply_RejectsAnEvenFunction(int function)
    {
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => SecsMessage.Create(1, (byte)function, replyExpected: false).CreateReply());
        Assert.Contains("CreateReply", error.Message);
    }

    [Fact]
    public void CreateReply_RejectsAFunctionThatCannotBeIncremented()
    {
        // 255 是最后一个可用的 Function，+1 就越出了字节范围，应答无从表示。
        Assert.Throws<InvalidOperationException>(() => SecsMessage.Create(1, 255, replyExpected: true).CreateReply());
    }
}