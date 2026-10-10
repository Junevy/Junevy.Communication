using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 位读写（P2-D10）：位软元件、字中的位、读-改-写的开关与警告，以及操作锁。
/// </summary>
public sealed class PlcClientBitTests
{
    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_BitDevice_ReadsPointsWithBitsAccess()
    {
        var device = new FakeDevice();
        device.SetBit('M', 10, true);
        device.SetBit('M', 12, true);
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("M10", 3);

        Assert.Equal(new[] { true, false, true }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadBitsOp, 10, 3) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_BitDeviceBeyondOnePacket_SplitsByPointLimit()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel, maxBits: 16);

        CommResult<bool[]> result = await client.ReadBitsAsync("M0", 17);

        Assert.True(result.IsSuccess);
        Assert.Equal(17, result.Data!.Length);
        Assert.Equal(new[] { (FakeFrame.ReadBitsOp, 0, 16), (FakeFrame.ReadBitsOp, 16, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_WordDeviceWithBitIndex_CrossesTheWordBoundary()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x8000);   // 位 15 为 1
        device.SetWord('D', 101, 0x0001);   // 位 0 为 1
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("D100.15", 2);

        Assert.Equal(new[] { true, true }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 2) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_WordDeviceWithoutBitIndex_UsesBitZero()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x0005);
        var client = new FakePlcClient(ChannelFor(device));

        CommResult<bool[]> result = await client.ReadBitsAsync("D100", 3);

        Assert.Equal(new[] { true, false, true }, result.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_BitDevice_WritesPointsWithoutReadModifyWrite()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteBitsAsync("M10", new[] { true, false, true });

        Assert.True(result.IsSuccess);
        Assert.True(device.GetBit('M', 10));
        Assert.False(device.GetBit('M', 11));
        Assert.True(device.GetBit('M', 12));
        Assert.Equal(new[] { (FakeFrame.WriteBitsOp, 10, 3) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_WordDeviceWithoutReadModifyWriteOption_ReturnsNotSupportedWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteBitsAsync("D100.3", new[] { false });

        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_WordDeviceWithReadModifyWriteOption_ChangesOnlyTheTargetBitAndWarns()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0xFFFF);
        var channel = ChannelFor(device);
        var logger = new TestLogger();
        var client = new FakePlcClient(channel, new PlcClientOptions { AllowReadModifyWriteBits = true }, logger: logger);

        CommResult result = await client.WriteBitsAsync("D100.3", new[] { false });

        Assert.True(result.IsSuccess);
        Assert.Equal((ushort)0xFFF7, device.GetWord('D', 100));
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 1), (FakeFrame.WriteWordsOp, 100, 1) }, Headers(channel));
        Assert.Single(logger.GetEntries(LogLevel.Warning));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_WordDeviceSpanningTwoWords_UpdatesBothWords()
    {
        var device = new FakeDevice();
        var client = new FakePlcClient(ChannelFor(device), new PlcClientOptions { AllowReadModifyWriteBits = true });

        CommResult result = await client.WriteBitsAsync("D100.15", new[] { true, true });

        Assert.True(result.IsSuccess);
        Assert.Equal((ushort)0x8000, device.GetWord('D', 100));
        Assert.Equal((ushort)0x0001, device.GetWord('D', 101));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadModifyWrite_ConcurrentOperations_DoNotInterleave()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new FakePlcClient(channel, new PlcClientOptions { AllowReadModifyWriteBits = true });

        // 两个读-改-写并发执行：请求顺序必须是 读 写 读 写，而不是 读 读 写 写。
        await Task.WhenAll(client.WriteBitsAsync("D100.0", new[] { true }), client.WriteBitsAsync("D200.0", new[] { true }));

        byte[] operations = channel.Requests.Select(request => FakeFrame.ReadHeader(request).Op).ToArray();
        Assert.Equal(new[] { FakeFrame.ReadWordsOp, FakeFrame.WriteWordsOp, FakeFrame.ReadWordsOp, FakeFrame.WriteWordsOp }, operations);
    }

}
