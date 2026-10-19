using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 类型化读写扩展（计划 Task 5）：8 种数值类型的单值与数组往返、与手册一致的字布局、字符串与位、失败透传。
/// </summary>
public sealed class PlcClientExtensionsTests
{
    [Theory(Timeout = 20000)]
    [InlineData(WordOrder.DCBA)]
    [InlineData(WordOrder.ABCD)]
    public async Task EveryNumericType_RoundTripsAsSingleValueAndArray(WordOrder order)
    {
        var device = new FakeDevice();
        var client = new FakePlcClient(ChannelFor(device), dataFormat: new PlcDataFormat(order));

        Assert.True((await client.WriteInt16Async("D100", -12345)).IsSuccess);
        Assert.Equal((short)-12345, (await client.ReadInt16Async("D100")).Data);
        Assert.True((await client.WriteUInt16Async("D110", 54321)).IsSuccess);
        Assert.Equal((ushort)54321, (await client.ReadUInt16Async("D110")).Data);
        Assert.True((await client.WriteInt32Async("D120", -123456789)).IsSuccess);
        Assert.Equal(-123456789, (await client.ReadInt32Async("D120")).Data);
        Assert.True((await client.WriteUInt32Async("D130", 0xDEADBEEFu)).IsSuccess);
        Assert.Equal(0xDEADBEEFu, (await client.ReadUInt32Async("D130")).Data);
        Assert.True((await client.WriteInt64Async("D140", -1234567890123456789L)).IsSuccess);
        Assert.Equal(-1234567890123456789L, (await client.ReadInt64Async("D140")).Data);
        Assert.True((await client.WriteUInt64Async("D150", 0xFEDCBA9876543210UL)).IsSuccess);
        Assert.Equal(0xFEDCBA9876543210UL, (await client.ReadUInt64Async("D150")).Data);
        Assert.True((await client.WriteFloatAsync("D160", -1.5f)).IsSuccess);
        Assert.Equal(-1.5f, (await client.ReadFloatAsync("D160")).Data);
        Assert.True((await client.WriteDoubleAsync("D170", 3.141592653589793)).IsSuccess);
        Assert.Equal(3.141592653589793, (await client.ReadDoubleAsync("D170")).Data);

        Assert.True((await client.WriteInt16Async("D300", new short[] { 1, -2, 3 })).IsSuccess);
        Assert.Equal(new short[] { 1, -2, 3 }, (await client.ReadInt16Async("D300", 3)).Data);
        Assert.True((await client.WriteUInt16Async("D320", new ushort[] { 4, 5, 6 })).IsSuccess);
        Assert.Equal(new ushort[] { 4, 5, 6 }, (await client.ReadUInt16Async("D320", 3)).Data);
        Assert.True((await client.WriteInt32Async("D340", new[] { -7, 8 })).IsSuccess);
        Assert.Equal(new[] { -7, 8 }, (await client.ReadInt32Async("D340", 2)).Data);
        Assert.True((await client.WriteUInt32Async("D360", new uint[] { 9, 10 })).IsSuccess);
        Assert.Equal(new uint[] { 9, 10 }, (await client.ReadUInt32Async("D360", 2)).Data);
        Assert.True((await client.WriteInt64Async("D380", new[] { -11L, 12L })).IsSuccess);
        Assert.Equal(new[] { -11L, 12L }, (await client.ReadInt64Async("D380", 2)).Data);
        Assert.True((await client.WriteUInt64Async("D400", new ulong[] { 13, 14 })).IsSuccess);
        Assert.Equal(new ulong[] { 13, 14 }, (await client.ReadUInt64Async("D400", 2)).Data);
        Assert.True((await client.WriteFloatAsync("D420", new[] { 0.5f, 2.25f })).IsSuccess);
        Assert.Equal(new[] { 0.5f, 2.25f }, (await client.ReadFloatAsync("D420", 2)).Data);
        Assert.True((await client.WriteDoubleAsync("D440", new[] { 1.5, -2.75 })).IsSuccess);
        Assert.Equal(new[] { 1.5, -2.75 }, (await client.ReadDoubleAsync("D440", 2)).Data);
    }

    [Fact(Timeout = 20000)]
    public async Task Int32_DcbaPlacesTheLowWordFirst()
    {
        var device = new FakeDevice();
        var client = new FakePlcClient(ChannelFor(device), dataFormat: new PlcDataFormat(WordOrder.DCBA));

        CommResult result = await client.WriteInt32Async("D100", 0x12345678);

        Assert.True(result.IsSuccess);
        Assert.Equal((ushort)0x5678, device.GetWord('D', 100));
        Assert.Equal((ushort)0x1234, device.GetWord('D', 101));
    }

    [Fact(Timeout = 20000)]
    public async Task Single_0Point75_DcbaMatchesTheManualWordLayout()
    {
        // 手册 p.77：D0 = 0000H、D1 = 3F40H，按字小端传输。
        var device = new FakeDevice();
        var client = new FakePlcClient(ChannelFor(device), dataFormat: new PlcDataFormat(WordOrder.DCBA));

        await client.WriteFloatAsync("D100", 0.75f);

        Assert.Equal((ushort)0x0000, device.GetWord('D', 100));
        Assert.Equal((ushort)0x3F40, device.GetWord('D', 101));
        Assert.Equal(0.75f, (await client.ReadFloatAsync("D100")).Data);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteString_OddLength_PadsWithNulAndReadsBack()
    {
        var device = new FakeDevice();
        var client = new FakePlcClient(ChannelFor(device));

        CommResult written = await client.WriteStringAsync("D100", "ABC");
        CommResult<string> read = await client.ReadStringAsync("D100", 3);

        Assert.True(written.IsSuccess);
        Assert.Equal((ushort)0x4241, device.GetWord('D', 100));   // 'A' 'B'
        Assert.Equal((ushort)0x0043, device.GetWord('D', 101));   // 'C' 0x00
        Assert.Equal("ABC", read.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadString_StopsAtTheFirstNul()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x0041);   // 'A' 0x00
        device.SetWord('D', 101, 0x4443);   // 'C' 'D'
        var client = new FakePlcClient(ChannelFor(device));

        CommResult<string> read = await client.ReadStringAsync("D100", 4);

        Assert.Equal("A", read.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteString_EmptyOrNull_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult empty = await client.WriteStringAsync("D100", string.Empty);
        CommResult nullText = await client.WriteStringAsync("D100", null!);

        Assert.Equal(CommErrorKind.InvalidRequest, empty.ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, nullText.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Theory(Timeout = 20000)]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task ReadString_NonPositiveLength_ReturnsInvalidRequestWithoutSending(int byteLength)
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult<string> result = await client.ReadStringAsync("D100", byteLength);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task Bool_RoundTripsOneBit()
    {
        var device = new FakeDevice();
        var client = new FakePlcClient(ChannelFor(device));

        CommResult written = await client.WriteBoolAsync("M7", true);
        CommResult<bool> read = await client.ReadBoolAsync("M7");

        Assert.True(written.IsSuccess);
        Assert.True(device.GetBit('M', 7));
        Assert.True(read.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task EmptyArray_ReturnsInvalidRequestWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult<int[]> read = await client.ReadInt32Async("D100", 0);
        CommResult write = await client.WriteInt32Async("D100", Array.Empty<int>());

        Assert.Equal(CommErrorKind.InvalidRequest, read.ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, write.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task FailureFromTheProtocol_IsReturnedUnchanged()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Fail("MC end code 0xC051.", CommErrorKind.RemoteError, 0xC051));
        channel.Enqueue(CommResult<byte[]>.Fail("MC end code 0xC051.", CommErrorKind.RemoteError, 0xC051));
        var client = new FakePlcClient(channel);

        CommResult<double> read = await client.ReadDoubleAsync("D100");
        CommResult write = await client.WriteDoubleAsync("D100", 1.0);

        Assert.Equal(CommErrorKind.RemoteError, read.ErrorKind);
        Assert.Equal(0xC051, read.ProtocolErrorCode);
        Assert.Equal("MC end code 0xC051.", read.ErrorMessage);
        Assert.Equal(CommErrorKind.RemoteError, write.ErrorKind);
        Assert.Equal(0xC051, write.ProtocolErrorCode);
    }

    [Fact(Timeout = 20000)]
    public async Task NullClient_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IPlcClient)null!).ReadInt16Async("D100"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IPlcClient)null!).WriteStringAsync("D100", "A"));
    }
}
