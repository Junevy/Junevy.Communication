using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 字读写：拆包边界、数据顺序、入口校验、取消与 ValidateRange。
/// </summary>
public sealed class PlcClientWordTests
{
    [Fact(Timeout = 20000)]
    public async Task ReadAsync_AtPacketLimit_SendsOnePacket()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100", FakePlcClient.DefaultMaxWords);

        Assert.True(result.IsSuccess);
        Assert.Equal(FakePlcClient.DefaultMaxWords * 2, result.Data!.Length);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 4) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadAsync_OneWordOverPacketLimit_SendsTwoPacketsWithCorrectOffsets()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100", FakePlcClient.DefaultMaxWords + 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 4), (FakeFrame.ReadWordsOp, 104, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadAsync_AcrossPackets_ReturnsWordsInAddressOrder()
    {
        var device = new FakeDevice();
        for (int i = 0; i < 5; i++)
            device.SetWord('D', 100 + i, (ushort)(i + 1));
        var client = new FakePlcClient(ChannelFor(device));

        CommResult<byte[]> result = await client.ReadAsync("D100", 5);

        // 默认字序 DCBA：每字小端。
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0, 5, 0 }, result.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadAsync_SecondPacketFails_ReturnsFailureWithoutPartialData()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 1, 0, 2, 0, 3, 0, 4, 0 }));
        channel.Enqueue(CommResult<byte[]>.Fail("MC end code 0xC051.", CommErrorKind.RemoteError, 0xC051));
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100", 5);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(0xC051, result.ProtocolErrorCode);
        Assert.Null(result.Data);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadAsync_ResponseLengthMismatch_ReturnsProtocolViolation()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 1, 0 }));
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100", 2);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Null(result.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteAsync_SplitsIntoPacketsWithCorrectNumbersAndPayloads()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteAsync("D100", new byte[] { 1, 0, 2, 0, 3, 0, 4, 0, 5, 0 });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteWordsOp, 100, 4), (FakeFrame.WriteWordsOp, 104, 1) }, Headers(channel));
        Assert.Equal((ushort)1, device.GetWord('D', 100));
        Assert.Equal((ushort)5, device.GetWord('D', 104));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteAsync_OddLength_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteAsync("D100", new byte[] { 1, 2, 3 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteAsync_EmptyData_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteAsync("D100", Array.Empty<byte>());

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Theory(Timeout = 20000)]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ReadAsync_CountBelowOne_ReturnsInvalidRequestWithoutSending(int count)
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100", count);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadAsync_AddressWithBitIndex_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100.5", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadAsync_UnknownDevice_ReturnsTheParserMessage()
    {
        var client = new FakePlcClient(ChannelFor(new FakeDevice()));

        CommResult<byte[]> result = await client.ReadAsync("Q1", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("Unknown device in address 'Q1'.", result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public async Task ValidateRange_Failure_IsReturnedWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new RangeLimitedClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100", 2);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("Range exceeded.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadAsync_CancelledBeforeTheOperation_ReturnsCancelledWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        CommResult<byte[]> result = await client.ReadAsync("D100", 1, cancellation.Token);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    // 只覆写 ValidateRange，用于验证它在发送前执行。
    private sealed class RangeLimitedClient : FakePlcClient
    {
        public RangeLimitedClient(ScriptedClientChannel channel)
            : base(channel)
        {
        }

        protected internal override CommResult ValidateRange(TestAddress address, PlcAccess access, int count)
            => CommResult.Fail("Range exceeded.", CommErrorKind.InvalidRequest);
    }
}
