using Junevy.Communication.Core.Results;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 字与位软元件上的按字节访问（P4-D4）：读取向上取整到整字后截断、写入只允许整字、拆包与范围检查按字计。
/// </summary>
public sealed class PlcClientByteOverWordsTests
{
    private const string WholeWordsMessage = "Byte access on a word or bit device can only write whole words; the data length must be a positive even number.";

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_WordDevice_OddLength_ReadsCoveringWordsAndTruncates()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x0201);
        device.SetWord('D', 101, 0x0403);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("D100", 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 2) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_WordDevice_EvenLength_ReadsWholeWords()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x0201);
        device.SetWord('D', 101, 0x0403);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("D100", 4);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 2) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_WordDevice_SplitsByWordsNotByBytes()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("D100", FakePlcClient.DefaultMaxWords * 2 + 2);

        // 10 个字节 = 5 个字，单包上限 4 个字：第二包只有 1 个字（2 个字节）。
        Assert.Equal(10, result.Data!.Length);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 4), (FakeFrame.ReadWordsOp, 104, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_WordDevice_CountBelowOne_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("D100", 0);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The count must be at least 1.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_WordDevice_OddLength_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("D100", new byte[] { 1, 2, 3 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(WholeWordsMessage, result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_WordDevice_EvenLength_WritesWholeWords()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("D100", new byte[] { 1, 0, 2, 0 });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteWordsOp, 100, 2) }, Headers(channel));
        Assert.Equal((ushort)1, device.GetWord('D', 100));
        Assert.Equal((ushort)2, device.GetWord('D', 101));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_BitDevice_ReadsCoveringWords()
    {
        var device = new FakeDevice();
        device.SetWord('M', 100, 0x0201);
        device.SetWord('M', 101, 0x0403);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("M100", 3);

        Assert.Equal(new byte[] { 1, 2, 3 }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 2) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_BitDevice_OddLength_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("M100", new byte[] { 1 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(WholeWordsMessage, result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ValidateRange_ReceivesByteAccessWithByteCountOnByteDevicesAndWordCountOtherwise()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new RecordingRangeClient(channel);

        await client.ReadBytesAsync("B100", 5);
        await client.ReadBytesAsync("D100", 5);
        await client.WriteBytesAsync("M100", new byte[] { 1, 2, 3, 4 });

        Assert.Equal(new[] { (PlcAccess.Bytes, 5), (PlcAccess.Words, 3), (PlcAccess.Words, 2) }, client.Ranges);
    }

    // 记录 ValidateRange 的粒度与点数，用于断言字节访问在哪条路径上按什么单位校验。
    private sealed class RecordingRangeClient : ByteFakePlcClient
    {
        private readonly List<(PlcAccess Access, int Count)> ranges = new List<(PlcAccess Access, int Count)>();

        public RecordingRangeClient(ScriptedClientChannel channel)
            : base(channel)
        {
        }

        public IReadOnlyList<(PlcAccess Access, int Count)> Ranges => ranges;

        protected internal override CommResult ValidateRange(TestAddress address, PlcAccess access, int count)
        {
            ranges.Add((access, count));
            return base.ValidateRange(address, access, count);
        }
    }
}
