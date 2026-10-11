using Junevy.Communication.Core.Results;
using static Junevy.Communication.Plc.Tests.PlcTestHelpers;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 位访问的路由（P4-D6）：字节软元件读位走字节钩子并按字节的位序取位；写位在协议支持原生位写入时走写位钩子，
/// 否则字软元件按现有规则、字节软元件返回 <c>NotSupported</c>。
/// </summary>
public sealed class PlcClientBitRoutingTests
{
    private const string ByteWriteBitsMessage =
        "Writing bits of a byte device is not supported; write the whole byte with WriteBytesAsync.";

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_ByteDevice_ReadsTheCoveringBytesThroughByteHooks()
    {
        var device = new FakeDevice();
        device.SetByte('B', 100, 0x05);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("B100", 3);

        Assert.Equal(new[] { true, false, true }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_ByteDevice_FromBitFive_ReadsOneByte()
    {
        var device = new FakeDevice();
        device.SetByte('B', 100, 0x05);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("B100.5", 3);

        Assert.Equal(new[] { false, false, false }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 1) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_ByteDevice_CrossingBytes_TakesBitsInByteOrder()
    {
        var device = new FakeDevice();
        device.SetByte('B', 100, 0x05);
        device.SetByte('B', 101, 0x03);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("B100.6", 4);

        Assert.Equal(new[] { false, false, true, true }, result.Data);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 2) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_ByteDevice_AcrossPackets_SplitsByBytes()
    {
        var device = new FakeDevice();
        device.SetByte('B', 106, 0x03);
        var channel = ChannelFor(device);
        var client = new ByteFakePlcClient(channel);

        // 50 个点需要 7 个字节，单包上限 6 个字节：拆成 6 + 1 两包。
        CommResult<bool[]> result = await client.ReadBitsAsync("B100", 50);

        Assert.Equal(50, result.Data!.Length);
        Assert.Equal(new[] { (FakeFrame.ReadBytesOp, 100, 6), (FakeFrame.ReadBytesOp, 106, 1) }, Headers(channel));
        Assert.True(result.Data[48]);
        Assert.True(result.Data[49]);
        Assert.False(result.Data[47]);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBitsAsync_ByteDevice_ValidateRangeReceivesTheCoveringByteCount()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new NativeByteBitsClient(channel);

        await client.ReadBitsAsync("B100.6", 4);

        Assert.Equal(new[] { (PlcAccess.Bytes, 2) }, client.Ranges);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_ByteDevice_WithoutNativeSupport_ReturnsNotSupportedWithoutSending()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new ByteFakePlcClient(channel, new PlcClientOptions { AllowReadModifyWriteBits = true });

        CommResult result = await client.WriteBitsAsync("B100", new[] { true });

        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.Equal(ByteWriteBitsMessage, result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_WordDevice_WithNativeSupport_UsesBitHooksWithoutReading()
    {
        var device = new FakeDevice();
        var channel = ChannelFor(device);
        var client = new NativeBitsClient(channel);

        CommResult result = await client.WriteBitsAsync("D100", new[] { true, false, true });

        Assert.True(result.IsSuccess);
        // 只有写位请求，没有任何读请求：原生位写入不需要读-改-写，也不看 AllowReadModifyWriteBits。
        Assert.Equal(new[] { (PlcAccess.Bits, 3) }, client.Ranges);
        Assert.Equal(new[] { (FakeFrame.WriteBitsOp, 100, 3) }, Headers(channel));
        Assert.Equal(new byte[] { 1, 0, 1 }, channel.Requests[0].AsSpan(FakeFrame.HeaderLength).ToArray());
        Assert.Equal((ushort)0, device.GetWord('D', 100));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_ByteDevice_WithNativeSupport_UsesBitHooksWithoutReading()
    {
        var channel = ChannelFor(new FakeDevice());
        var client = new NativeByteBitsClient(channel);

        CommResult result = await client.WriteBitsAsync("B100.5", new[] { true, false });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { (PlcAccess.Bits, 2) }, client.Ranges);
        Assert.Equal(new[] { (FakeFrame.WriteBitsOp, 100, 2) }, Headers(channel));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBitsAsync_WordDevice_WithoutNativeSupport_KeepsTheExistingRules()
    {
        var plainChannel = ChannelFor(new FakeDevice());
        var plain = new ByteFakePlcClient(plainChannel);
        var modifyChannel = ChannelFor(new FakeDevice());
        var modify = new ByteFakePlcClient(modifyChannel, new PlcClientOptions { AllowReadModifyWriteBits = true });

        CommResult withoutOption = await plain.WriteBitsAsync("D100", new[] { true });
        CommResult withOption = await modify.WriteBitsAsync("D100", new[] { true });

        Assert.Equal(CommErrorKind.NotSupported, withoutOption.ErrorKind);
        Assert.True(withOption.IsSuccess);
        Assert.Empty(plainChannel.Requests);
        Assert.Equal(new[] { (FakeFrame.ReadWordsOp, 100, 1), (FakeFrame.WriteWordsOp, 100, 1) }, Headers(modifyChannel));
    }

    // 字软元件 + 原生位写入：记录 ValidateRange 收到的粒度与点数。
    private sealed class NativeBitsClient : FakePlcClient
    {
        private readonly List<(PlcAccess Access, int Count)> ranges = new List<(PlcAccess Access, int Count)>();

        public NativeBitsClient(ScriptedClientChannel channel)
            : base(channel)
        {
        }

        public IReadOnlyList<(PlcAccess Access, int Count)> Ranges => ranges;

        protected internal override bool CanWriteBitsNatively(TestAddress address) => true;

        protected internal override CommResult ValidateRange(TestAddress address, PlcAccess access, int count)
        {
            ranges.Add((access, count));
            return base.ValidateRange(address, access, count);
        }
    }

    // 字节软元件 + 原生位写入：字节与位两种访问都在同一次操作锁下按点或按字节校验。
    private sealed class NativeByteBitsClient : ByteFakePlcClient
    {
        private readonly List<(PlcAccess Access, int Count)> ranges = new List<(PlcAccess Access, int Count)>();

        public NativeByteBitsClient(ScriptedClientChannel channel)
            : base(channel)
        {
        }

        public IReadOnlyList<(PlcAccess Access, int Count)> Ranges => ranges;

        protected internal override bool CanWriteBitsNatively(TestAddress address) => true;

        protected internal override CommResult ValidateRange(TestAddress address, PlcAccess access, int count)
        {
            ranges.Add((access, count));
            return base.ValidateRange(address, access, count);
        }
    }
}
