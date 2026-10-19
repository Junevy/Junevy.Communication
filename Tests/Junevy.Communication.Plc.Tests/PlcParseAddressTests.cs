using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// <see cref="IPlcClient.ParseAddress"/> 的契约（计划 P4-D4）：同步、无 I/O、释放后仍可调用；非法地址返回 <c>InvalidRequest</c>。
/// </summary>
public sealed class PlcParseAddressTests
{
    [Fact]
    public void ParseAddress_OnTheInterface_ReturnsTheParsedAddressWithoutSending()
    {
        var channel = new ScriptedClientChannel();
        IPlcClient client = new FakePlcClient(channel);

        CommResult<PlcAddress> result = client.ParseAddress(" D100.5 ");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("D100.5", result.Data!.Text);
        Assert.Equal(PlcAddressUnit.Word, result.Data.Unit);
        Assert.Equal(100, result.Data.Number);
        Assert.Equal(5, result.Data.BitIndex);
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public void ParseAddress_ByteDevice_ReportsTheByteUnit()
    {
        var client = new FakePlcClient(new ScriptedClientChannel());

        CommResult<PlcAddress> result = client.ParseAddress("B10.7");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(PlcAddressUnit.Byte, result.Data!.Unit);
        Assert.Equal(7, result.Data.BitIndex);
    }

    [Fact]
    public void ParseAddress_InvalidText_ReturnsInvalidRequestWithTheParserMessage()
    {
        var client = new FakePlcClient(new ScriptedClientChannel());

        CommResult<PlcAddress> result = client.ParseAddress("Q100");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Null(result.Data);
        Assert.Equal("Unknown device in address 'Q100'.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseAddress_EmptyOrNullText_ReturnsInvalidRequest(string? text)
    {
        var client = new FakePlcClient(new ScriptedClientChannel());

        CommResult<PlcAddress> result = client.ParseAddress(text!);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The address is empty.", result.ErrorMessage);
    }

    [Fact]
    public void ParseAddress_AfterDispose_StillParses()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);
        client.Dispose();

        CommResult<PlcAddress> ok = client.ParseAddress("D100");
        CommResult<PlcAddress> bad = client.ParseAddress("Q100");

        Assert.True(ok.IsSuccess, ok.ErrorMessage);
        Assert.Equal(CommErrorKind.InvalidRequest, bad.ErrorKind);
        Assert.Empty(channel.Requests);
    }
}
