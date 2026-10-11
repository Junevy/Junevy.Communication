using System.Text;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 字符串扩展按软元件单位的行为（P4-D7）：字与位软元件上保持 P2 的整字规则，字节软元件精确写入编码后的字节；
/// 固定长度重载用 0x00 补满，用来清掉短串覆盖长串后的残留字符。
/// </summary>
public sealed class PlcClientStringByteTests
{
    private const string OddFixedLengthMessage =
        "A fixed string length on a word or bit device must be a positive even number.";

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_ByteDevice_OddLength_WritesExactlyTheEncodedBytes()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult written = await client.WriteStringAsync("B100", "ABC");

        Assert.True(written.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 100, 3) }, Headers(channel));
        Assert.Equal((byte)'A', device.GetByte('B', 100));
        Assert.Equal((byte)'C', device.GetByte('B', 102));
        // 没有补 0x00：第 4 个字节未被触碰。
        Assert.Equal((byte)0, device.GetByte('B', 103));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_ByteDevice_ReadsBackThroughByteHooks()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        await client.WriteStringAsync("B100", "ABC");
        CommResult<string> read = await client.ReadStringAsync("B100", 3);

        Assert.Equal("ABC", read.Data);
        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 100, 3), (FakeFrame.ReadBytesOp, 100, 3) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_WordDevice_OddLength_StillPadsWithNul()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult written = await client.WriteStringAsync("D100", "A");

        Assert.True(written.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteWordsOp, 100, 1) }, Headers(channel));
        Assert.Equal((ushort)0x0041, device.GetWord('D', 100));   // 'A' 0x00
    }

    [Fact(Timeout = 20000)]
    public async Task ReadStringAsync_WordDevice_ReadsTheCoveringWords()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x4241);   // 'A' 'B'
        device.SetWord('D', 101, 0x0043);   // 'C' 0x00
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult<string> read = await client.ReadStringAsync("D100", 3);

        // 与 P2 相同：3 个字节读 2 个字，取前 3 个字节解码，并在 0x00 处截断。
        Assert.Equal("ABC", read.Data);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 2) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadStringAsync_BitDevice_ReadsTheCoveringWords()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult written = await client.WriteStringAsync("M100", "AB");
        CommResult<string> read = await client.ReadStringAsync("M100", 2);

        Assert.True(written.IsSuccess);
        Assert.Equal("AB", read.Data);
        Assert.Equal(new[] { (FakeFrame.WriteWordsOp, 100, 1), (FakeFrame.ReadWordsOp, 100, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_FixedLength_PadsWithNulAndClearsTheResidualCharacters()
    {
        var device = new FakeDevice();
        var client = new ByteFakePlcClient(ChannelFor(device));

        await client.WriteStringAsync("B100", "ABCDEF");
        CommResult written = await client.WriteStringAsync("B100", "XY", 6);
        CommResult<string> read = await client.ReadStringAsync("B100", 6);

        Assert.True(written.IsSuccess);
        Assert.Equal("XY", read.Data);
        Assert.Equal((byte)'X', device.GetByte('B', 100));
        Assert.Equal((byte)'Y', device.GetByte('B', 101));
        Assert.Equal(new byte[] { 0, 0, 0, 0 },
                     new[] { device.GetByte('B', 102), device.GetByte('B', 103), device.GetByte('B', 104), device.GetByte('B', 105) });
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_FixedLength_OddLengthOnByteDevice_WritesThatManyBytes()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult written = await client.WriteStringAsync("B100", "XY", 5);

        Assert.True(written.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 100, 5) }, Headers(channel));
        Assert.Equal((byte)0, device.GetByte('B', 102));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_FixedLength_FullLength_WritesNoPadding()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        await client.WriteStringAsync("B100", "ABC", 3);

        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 100, 3) }, Headers(channel));
        Assert.Equal((byte)0, device.GetByte('B', 103));
    }

    [Theory(Timeout = 20000)]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task WriteStringAsync_FixedLength_InvalidLengthOrTooLongValue_ReturnsInvalidRequestWithoutSending(int byteLength)
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        // "ABCDEF" 编码为 6 个字节，只在不放得下或长度不合法时才失败。
        CommResult result = await client.WriteStringAsync("B100", "ABCDEF", byteLength);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_FixedLength_TooLong_MessageStatesBothLengths()
    {
        var client = new ByteFakePlcClient(ChannelFor(new FakeDevice()));

        CommResult result = await client.WriteStringAsync("B100", "ABCDEF", 3);

        Assert.Equal("The string encodes to 6 byte(s), which does not fit the fixed field length of 3 byte(s).", result.ErrorMessage);
    }

    [Theory(Timeout = 20000)]
    [InlineData("D100")]
    [InlineData("M100")]
    public async Task WriteStringAsync_FixedLength_OddLengthOnWordOrBitDevice_ReturnsInvalidRequest(string address)
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteStringAsync(address, "AB", 5);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(OddFixedLengthMessage, result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_FixedLength_EvenLengthOnWordDevice_WritesWholeWords()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult written = await client.WriteStringAsync("D100", "ABC", 4);

        Assert.True(written.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteWordsOp, 100, 2) }, Headers(channel));
        Assert.Equal((ushort)0x4241, device.GetWord('D', 100));
        Assert.Equal((ushort)0x0043, device.GetWord('D', 101));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteStringAsync_InvalidAddress_ReturnsTheParserMessageWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult free = await client.WriteStringAsync("Z100", "AB");
        CommResult fixedLength = await client.WriteStringAsync("Z100", "AB", 4);
        CommResult<string> read = await client.ReadStringAsync("Z100", 4);

        Assert.Equal(CommErrorKind.InvalidRequest, free.ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, fixedLength.ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, read.ErrorKind);
        Assert.Contains("Z100", free.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task String_UsesTheEncodingFromDataFormat_OnByteDevices()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new Utf8ByteFakePlcClient(channel);

        CommResult written = await client.WriteStringAsync("B200", "é");
        CommResult<string> read = await client.ReadStringAsync("B200", 2);

        // 'é' 在 UTF-8 下是 2 个字节（0xC3 0xA9），ASCII 下是 1 个 '?'：编码取 DataFormat.StringEncoding。
        Assert.True(written.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 200, 2), (FakeFrame.ReadBytesOp, 200, 2) }, Headers(channel));
        Assert.Equal((byte)0xC3, device.GetByte('B', 200));
        Assert.Equal((byte)0xA9, device.GetByte('B', 201));
        Assert.Equal("é", read.Data);
    }

    // 只改编码的测试协议：证明字符串扩展取 DataFormat.StringEncoding，而不是固定用 ASCII。
    private sealed class Utf8ByteFakePlcClient : ByteFakePlcClient
    {
        public Utf8ByteFakePlcClient(ScriptedClientChannel channel)
            : base(channel, dataFormat: new PlcDataFormat(WordOrder.DCBA, Encoding.UTF8))
        {
        }
    }
}
