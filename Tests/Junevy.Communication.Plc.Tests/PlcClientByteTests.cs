using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 字节软元件的按字节读写（P4-D4）：入口校验、按字节拆包、数据顺序，以及协议未实现字节钩子时的失败。
/// </summary>
public sealed class PlcClientByteTests
{
    private const string ReadBitIndexMessage = "A bit index is not allowed in ReadBytesAsync; use ReadBitsAsync to read single bits.";
    private const string WriteBitIndexMessage = "A bit index is not allowed in WriteBytesAsync; use WriteBitsAsync to write single bits.";

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_AtPacketLimit_SendsOnePacket()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", ByteFakePlcClient.DefaultMaxBytes);

        Assert.True(result.IsSuccess);
        Assert.Equal(ByteFakePlcClient.DefaultMaxBytes, result.Data!.Length);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 6) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_OneByteOverPacketLimit_SendsTwoPacketsWithByteOffsets()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", ByteFakePlcClient.DefaultMaxBytes + 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Data!.Length);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 6), (FakeFrame.ReadBytesOp, 106, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_TwoPacketLimitsPlusOne_SendsThreePackets()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", ByteFakePlcClient.DefaultMaxBytes * 2 + 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(13, result.Data!.Length);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 6), (FakeFrame.ReadBytesOp, 106, 6), (FakeFrame.ReadBytesOp, 112, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_ReturnsDeviceBytesInMemoryOrder()
    {
        var device = new FakeDevice();
        for (int i = 0; i < 5; i++)
            device.SetByte('B', 100 + i, (byte)(i + 1));
        var client = new ByteFakePlcClient(ChannelFor(device));

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", 5);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_SplitsIntoPacketsWithCorrectNumbersAndPayloads()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("B100", new byte[] { 1, 2, 3, 4, 5, 6, 7 });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 100, 6), (FakeFrame.WriteBytesOp, 106, 1) }, Headers(channel));
        Assert.Equal((byte)1, device.GetByte('B', 100));
        Assert.Equal((byte)7, device.GetByte('B', 106));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_OddLength_WritesExactByteCount()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("B100", new byte[] { 1, 2, 3 });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { (FakeFrame.WriteBytesOp, 100, 3) }, Headers(channel));
        Assert.Equal((byte)3, device.GetByte('B', 102));
        Assert.Equal((byte)0, device.GetByte('B', 103));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_ResponseLengthMismatch_ReturnsProtocolViolation()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { FakeFrame.StatusOk }));
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", 2);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Null(result.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_SecondPacketFails_ReturnsFailureWithoutPartialData()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { FakeFrame.StatusOk, 1, 2, 3, 4, 5, 6 }));
        channel.Enqueue(CommResult<byte[]>.Fail("The device returned status 0xC1.", CommErrorKind.RemoteError, 0xC1));
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", 7);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(0xC1, result.ProtocolErrorCode);
        Assert.Null(result.Data);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_SecondPacketFails_StopsAfterThatPacket()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { FakeFrame.StatusOk }));
        channel.Enqueue(CommResult<byte[]>.Fail("The device returned status 0xC1.", CommErrorKind.RemoteError, 0xC1));
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("B100", new byte[] { 1, 2, 3, 4, 5, 6, 7 });

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(2, channel.Requests.Count);
        Assert.Equal(106, FakeFrame.ReadHeader(channel.Requests[1]).Number);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_EmptyData_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("B100", Array.Empty<byte>());

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The data must not be empty.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Theory(Timeout = 20000)]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ReadBytesAsync_CountBelowOne_ReturnsInvalidRequestWithoutSending(int count)
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", count);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The count must be at least 1.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_AddressWithBitIndex_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100.3", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(ReadBitIndexMessage, result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_AddressWithBitIndex_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("B100.3", new byte[] { 1 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(WriteBitIndexMessage, result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_CountAboveByteLimit_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", int.MaxValue);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The count is too large.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBytesAsync_ProtocolWithoutByteHooks_ReturnsUnspecifiedAndLogsError()
    {
        var channel = ChannelFor(new FakeDevice());
        var logger = new TestLogger();
        var client = new FakePlcClient(channel, logger: logger);

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", 4);

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Equal("The operation failed unexpectedly during ReadBytesAsync.", result.ErrorMessage);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Single(logger.GetEntries(LogLevel.Error));
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_ProtocolWithoutByteHooks_ReturnsUnspecifiedAndLogsError()
    {
        var channel = ChannelFor(new FakeDevice());
        var logger = new TestLogger();
        var client = new FakePlcClient(channel, logger: logger);

        CommResult result = await client.WriteBytesAsync("B100", new byte[] { 1, 2 });

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Equal("The operation failed unexpectedly during WriteBytesAsync.", result.ErrorMessage);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Single(logger.GetEntries(LogLevel.Error));
        Assert.Empty(channel.Requests);
    }
}
