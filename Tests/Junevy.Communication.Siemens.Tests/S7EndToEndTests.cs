using Junevy.Communication.Plc;
using Junevy.Communication.Siemens.Protocol;
using static Junevy.Communication.Siemens.Tests.S7RequestFrame;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 端到端的数据通路（计划 Task 10 第 2 条，真套接字）：握手与协商值、Int16 / Int32 / Float / Double 的大端往返、
/// 奇数长度的字节读写、通用字符串与 S7 的 String / WString、跨字节的位读与原生位写，以及 PDU 240 下的拆包边界。
/// 设备侧一律由 <see cref="S7ConnectedCpu"/> 的字节内存回答，因此这里断言的是"设备真正收到的字节"。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class S7EndToEndTests
{
    /// <summary>P4-D12：PDU 240 时一包最多 222 字节。</summary>
    private const int ReadLimit = 222;

    /// <summary>P4-D12：PDU 240 时一包最多 212 字节。</summary>
    private const int WriteLimit = 212;

    [Theory(Timeout = 60000)]
    [InlineData(240)]
    [InlineData(960)]
    public async Task HandshakeNegotiatesThePdu_AndTypedValuesAreBigEndian(int pdu)
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync(pdu);
        Assert.Equal(pdu, cpu.Client.NegotiatedPduSize);
        Assert.Equal(1, cpu.Device.ConnectionRequestCount);

        Succeeded(await cpu.Client.WriteInt16Async("DB1.DBW0", -1234));
        Succeeded(await cpu.Client.WriteInt32Async("DB1.DBD2", 0x12345678));
        Succeeded(await cpu.Client.WriteFloatAsync("DB1.DBD6", 1.0f));
        Succeeded(await cpu.Client.WriteDoubleAsync("DB1.DBD10", 1.0));

        // S7 的设备内存一律高位在前（P4-D11）：-1234 = FB 2E，1.0f = 3F 80 00 00，1.0 = 3F F0 00 …。
        Assert.Equal(
            ToBytes("FB 2E 12 34 56 78 3F 80 00 00 3F F0 00 00 00 00 00 00"),
            cpu.Device.Block(S7Area.DataBlock, 1).Take(18).ToArray());
        Assert.Equal((short)-1234, Succeeded(await cpu.Client.ReadInt16Async("DB1.DBW0")));
        Assert.Equal(0x12345678, Succeeded(await cpu.Client.ReadInt32Async("DB1.DBD2")));
        Assert.Equal(1.0f, Succeeded(await cpu.Client.ReadFloatAsync("DB1.DBD6")));
        Assert.Equal(1.0, Succeeded(await cpu.Client.ReadDoubleAsync("DB1.DBD10")));
    }

    [Fact(Timeout = 60000)]
    public async Task OddLengthBytesRoundTrip_AndSingleByteAccess()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
        byte[] payload = ToBytes("01 02 03 04 05 06 07");

        Succeeded(await cpu.Client.WriteBytesAsync("DB1.DBB3", payload));

        // 7 个字节精确落到 DB1 的 3..9，两侧不补齐（P4-D7 只对字与位软元件按整点取字节）。
        Assert.Equal(ToBytes("00 00 00 01 02 03 04 05 06 07"), cpu.Device.Block(S7Area.DataBlock, 1).Take(10).ToArray());
        Assert.Equal(payload, Succeeded(await cpu.Client.ReadBytesAsync("DB1.DBB3", 7)));
        Assert.Equal((byte)0x05, Succeeded(await cpu.Client.ReadByteAsync("DB1.DBB7")));

        Succeeded(await cpu.Client.ReadBytesAsync("DB1.DBB1", 5));
        byte[] last = cpu.Device.Requests.Last(request => Service(request) == S7VarItem.ReadService);
        Assert.Equal(S7VarItem.RequestByte, TransportSize(last));
        Assert.Equal(5, Quantity(last));
        Assert.Equal(1, ByteAddress(last));
    }

    [Fact(Timeout = 60000)]
    public async Task GenericStringAndS7StringFieldsRoundTrip()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
        byte[] memory = cpu.Device.Block(S7Area.DataBlock, 1);

        Succeeded(await cpu.Client.WriteStringAsync("DB1.DBB20", "ABC"));
        Assert.Equal(ToBytes("41 42 43 00"), memory.AsSpan(20, 4).ToArray());
        Assert.Equal("ABC", Succeeded(await cpu.Client.ReadStringAsync("DB1.DBB20", 8)));

        // String 字段：头部是声明的最大长度与实际长度，字符按 DataFormat.StringEncoding（计划第 13 节第 3 条）。
        Succeeded(await cpu.Client.WriteS7StringAsync("DB1.DBB40", "AB", 10));
        Assert.Equal(ToBytes("0A 02 41 42"), memory.AsSpan(40, 4).ToArray());
        Assert.Equal("AB", Succeeded(await cpu.Client.ReadS7StringAsync("DB1.DBB40", 10)));

        // WString 字段：4 字节大端头部 + UTF-16 大端字符，"中" = 4E 2D。
        Succeeded(await cpu.Client.WriteS7WStringAsync("DB1.DBB60", "中", 4));
        Assert.Equal(ToBytes("00 04 00 01 4E 2D"), memory.AsSpan(60, 6).ToArray());
        Assert.Equal("中", Succeeded(await cpu.Client.ReadS7WStringAsync("DB1.DBB60", 4)));
    }

    [Fact(Timeout = 60000)]
    public async Task BitsCrossByteBorders_WhenReadAndAreWrittenNatively()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
        byte[] memory = cpu.Device.Block(S7Area.DataBlock, 1);
        memory[10] = 0x40;   // 位 6
        memory[11] = 0x01;   // 字节 11 的位 0，即跨字节后的第 3 个点

        // P4-D6：S7 没有位软元件，读位由套件改成按字节读，覆盖 2 个字节。
        Assert.Equal(new[] { true, false, true, false }, Succeeded(await cpu.Client.ReadBitsAsync("DB1.DBX10.6", 4)));
        byte[] read = cpu.Device.Requests.Last(request => Service(request) == S7VarItem.ReadService);
        Assert.Equal(S7VarItem.RequestByte, TransportSize(read));
        Assert.Equal(2, Quantity(read));

        // 写位是原生写入：地址项与数据项都用 BIT 传输尺寸，且一包只有一位（P4-D12）。
        Succeeded(await cpu.Client.WriteBitsAsync("DB1.DBX12.6", new[] { true, false, true }));

        Assert.Equal(new byte[] { 0x40, 0x01 }, memory.AsSpan(12, 2).ToArray());
        byte[][] writes = cpu.Device.Requests.Where(request => Service(request) == S7VarItem.WriteService).ToArray();
        Assert.Equal(new[] { 0x66, 0x67, 0x68 }, writes.Select(BitAddress).ToArray());
        Assert.All(writes, request =>
        {
            Assert.Equal(S7VarItem.RequestBit, TransportSize(request));
            Assert.Equal(S7VarItem.DataBit, DataTransportSize(request));
        });
    }

    [Theory(Timeout = 60000)]
    [InlineData(ReadLimit)]
    [InlineData(ReadLimit + 1)]
    public async Task ReadsAboveTheNegotiatedLimit_AreSplitAndReassembled(int count)
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
        byte[] memory = cpu.Device.Block(S7Area.DataBlock, 1);
        for (int i = 0; i < count; i++)
            memory[i] = (byte)(i % 251);

        byte[] data = Succeeded(await cpu.Client.ReadBytesAsync("DB1.DBB0", count));

        Assert.Equal(count, data.Length);
        Assert.Equal(Packets(count, ReadLimit), cpu.Device.Requests.Select(Quantity).ToArray());
        Assert.Equal(memory.Take(count).ToArray(), data);
    }

    [Theory(Timeout = 60000)]
    [InlineData(WriteLimit)]
    [InlineData(WriteLimit + 1)]
    public async Task WritesAboveTheNegotiatedLimit_AreSplitAndReassembled(int count)
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
        var payload = new byte[count];
        for (int i = 0; i < count; i++)
            payload[i] = (byte)(i % 251);

        Succeeded(await cpu.Client.WriteBytesAsync("DB1.DBB0", payload));

        Assert.Equal(Packets(count, WriteLimit), cpu.Device.Requests.Select(Quantity).ToArray());
        Assert.Equal(payload, cpu.Device.Block(S7Area.DataBlock, 1).Take(count).ToArray());
    }

    // 按每包上限切出的点数序列：223 → [222, 1]，222 → [222]。偏移由内容的正确性一起证明。
    private static int[] Packets(int total, int limit)
    {
        var sizes = new List<int>();
        for (int left = total; left > 0; left -= limit)
            sizes.Add(Math.Min(left, limit));

        return sizes.ToArray();
    }
}
