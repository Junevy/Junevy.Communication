using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 单字节读写扩展（P4-D7）：字节软元件精确读写 1 个字节；字与位软元件上读得到规范化布局的第一个字节，
/// 写因为只能整字写入而返回 <c>InvalidRequest</c>。
/// </summary>
public sealed class PlcClientSingleByteTests
{
    private const string WholeWordsMessage =
        "Byte access on a word or bit device can only write whole words; the data length must be a positive even number.";

    [Fact(Timeout = 20000)]
    public async Task ReadByteAsync_ByteDevice_ReadsOneByteThroughByteHooks()
    {
        var device = new FakeDevice();
        device.SetByte('B', 100, 0x2A);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult<byte> result = await client.ReadByteAsync("B100");

        Assert.Equal((byte)0x2A, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadByteAsync_WordDevice_ReturnsTheFirstByteOfTheNormalizedLayout()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x3412);   // DCBA 上线字节为 12 34，规范化布局的第一个字节是字的低字节
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult<byte> result = await client.ReadByteAsync("D100");

        Assert.Equal((byte)0x12, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadByteAsync_BitDevice_ReturnsTheFirstByteOfTheCoveringWord()
    {
        var device = new FakeDevice();
        device.SetWord('M', 100, 0x00FF);
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult<byte> result = await client.ReadByteAsync("M100");

        Assert.Equal((byte)0xFF, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteByteAsync_ByteDevice_RoundTrips()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult written = await client.WriteByteAsync("B100", 0x7F);
        CommResult<byte> read = await client.ReadByteAsync("B100");

        Assert.True(written.IsSuccess);
        Assert.Equal((byte)0x7F, read.Data);
        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 100, 1), (FakeFrame.ReadBytesOp, 100, 1) }, Headers(channel));
    }

    [Theory(Timeout = 20000)]
    [InlineData("D100")]
    [InlineData("M100")]
    public async Task WriteByteAsync_WordOrBitDevice_ReturnsInvalidRequestWithoutSending(string address)
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteByteAsync(address, 0x2A);

        // 单字节写落在字与位软元件上就是半个字，套件的字节写规则直接拒绝。
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(WholeWordsMessage, result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Theory(Timeout = 20000)]
    [InlineData("B100.3")]
    [InlineData("D100.5")]
    public async Task ReadByteAsync_AddressWithBitIndex_ReturnsInvalidRequestWithoutSending(string address)
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte> result = await client.ReadByteAsync(address);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task SingleByteAccess_InvalidAddress_ReturnsTheParserMessageWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte> read = await client.ReadByteAsync("Z100");
        CommResult write = await client.WriteByteAsync("Z100", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, read.ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, write.ErrorKind);
        Assert.Contains("Z100", read.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task SingleByteAccess_ProtocolWithoutByteHooks_ReturnsUnspecifiedAndLogsError()
    {
        var channel = ChannelFor(new FakeDevice());
        var logger = new TestLogger();
        var client = new FakePlcClient(channel, logger: logger);

        // 字节软元件上没有字节钩子的协议：套件的字节路径把默认实现的异常收口为 Unspecified。
        CommResult<byte> read = await client.ReadByteAsync("B100");

        Assert.Equal(CommErrorKind.Unspecified, read.ErrorKind);
        Assert.IsType<InvalidOperationException>(read.Exception);
        Assert.Single(logger.GetEntries(LogLevel.Error));
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task SingleByteAndFixedLengthExtensions_NullClient_Throws()
    {
        IPlcClient client = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ReadByteAsync("B100"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.WriteByteAsync("B100", 1));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.WriteStringAsync("B100", "AB", 4));
    }
}
