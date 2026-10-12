using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Plc;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 端到端的数据通路（计划 Task 14 第 2 条，真套接字）：Int16 / Int32 / Float 的往返与 CDAB 的黄金样例、
/// 位读覆盖整字与原生位写的跨字连续、字符串的 ASCII 落位、EM 组的区域代码，以及 999 / 996 字上限的拆包。
/// 设备侧一律由 <see cref="FinsConnectedPlc"/> 的字内存回答，因此这里断言的是"设备真正收到的字节"。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FinsEndToEndTests
{
    /// <summary>P4-D12：内存区读的单包上限（字访问按字计）。</summary>
    private const int ReadLimit = 999;

    /// <summary>P4-D12：内存区写的单包上限。</summary>
    private const int WriteLimit = 996;

    [Fact(Timeout = 60000)]
    public async Task TypedValuesRoundTrip_InTheCdabWordOrder()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        PlcMemory memory = plc.Device.Memory;

        Succeeded(await plc.Client.WriteInt16Async("D0", -1234));
        Succeeded(await plc.Client.WriteInt32Async("D10", 0x12345678));
        Succeeded(await plc.Client.WriteFloatAsync("D20", 10.0f));

        // P4-D11 的默认字序 CDAB：字内高位在前，双字值的低字放在低地址（10.0f = 4120 0000，高字因此落在 D21）。
        Assert.Equal((ushort)0xFB2E, memory.GetWords("D", 0, 1)[0]);
        Assert.Equal(new[] { (ushort)0x5678, (ushort)0x1234 }, memory.GetWords("D", 10, 2));
        Assert.Equal(new[] { (ushort)0x0000, (ushort)0x4120 }, memory.GetWords("D", 20, 2));

        // 黄金样例的反方向（计划 Task 14 第 2 条）：预置 D100 = 0x0000、D101 = 0x4120，读 Float 得 10.0。
        memory.SetWords("D", 100, 0x0000, 0x4120);
        Assert.Equal((short)-1234, Succeeded(await plc.Client.ReadInt16Async("D0")));
        Assert.Equal(0x12345678, Succeeded(await plc.Client.ReadInt32Async("D10")));
        Assert.Equal(10.0f, Succeeded(await plc.Client.ReadFloatAsync("D20")));
        Assert.Equal(10.0f, Succeeded(await plc.Client.ReadFloatAsync("D100")));
    }

    [Fact(Timeout = 60000)]
    public async Task BitReadsCoverWholeWords_AndNativeBitWritesContinueIntoTheNextWord()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        PlcMemory memory = plc.Device.Memory;
        memory.SetWords("D", 0, 0x8000, 0x0002);   // D0 的位 15 与 D1 的位 1（绝对位号 16 之后）

        // P4-D6：FINS 没有位软元件，读位由套件改成读覆盖的字，所以设备收到的仍是 D 字区的 2 字读。
        Assert.Equal(new[] { false, true, false, true }, Succeeded(await plc.Client.ReadBitsAsync("D0.14", 4)));
        byte[] read = LastRequest(plc.Device, FinsMemoryArea.CommandRead);
        Assert.Equal(DWordArea, AreaCodeOf(read));
        Assert.Equal(0, WordAddressOf(read));
        Assert.Equal(2, QuantityOf(read));

        // 原生位写用位区代码，每点 1 字节；后面的点跨入下一字的位号 0（17.2 节 V-F-7 的形状）。
        Succeeded(await plc.Client.WriteBitsAsync("D0.14", new[] { true, true, true }));
        byte[] write = LastRequest(plc.Device, FinsMemoryArea.CommandWrite);
        Assert.Equal(DBitArea, AreaCodeOf(write));
        Assert.Equal(0, WordAddressOf(write));
        Assert.Equal(14, BitNumberOf(write));
        Assert.Equal(3, QuantityOf(write));
        Assert.Equal(ToBytes("01 01 01"), write.AsSpan(RequestPayloadIndex).ToArray());

        // 位落回与字同一份内存：D0 的位 14、15 置起，D1 的位 0 置起且原有的位 1 保持。
        Assert.Equal(new[] { (ushort)0xC000, (ushort)0x0003 }, memory.GetWords("D", 0, 2));
        Assert.Equal(new[] { true, true, true }, Succeeded(await plc.Client.ReadBitsAsync("D0.14", 3)));
    }

    [Fact(Timeout = 60000)]
    public async Task StringsLandAsAsciiBytesInConsecutiveWords()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        PlcMemory memory = plc.Device.Memory;

        Succeeded(await plc.Client.WriteStringAsync("D100", "AB"));

        // P4-D11 的 ASCII 字符串：'A' 在字的高字节、'B' 在低字节，替身里即 0x4142。
        Assert.Equal(new[] { (ushort)0x4142 }, memory.GetWords("D", 100, 1));
        Assert.Equal("AB", Succeeded(await plc.Client.ReadStringAsync("D100", 2)));

        Succeeded(await plc.Client.WriteStringAsync("D110", "ABC"));

        // 奇数长度补到整字：D111 只有高字节是 'C'，低字节补 0x00。
        Assert.Equal(new[] { (ushort)0x4142, (ushort)0x4300 }, memory.GetWords("D", 110, 2));
        Assert.Equal("ABC", Succeeded(await plc.Client.ReadStringAsync("D110", 3)));
    }

    [Fact(Timeout = 60000)]
    public async Task EmGroupAccessUsesTheGroupAreaCode()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();

        Succeeded(await plc.Client.WriteInt16Async("E2_500", 0x1234));

        // P4-D20 ④：EM 第 n 组的字区代码为 0xA0 + n（该展开未经真机验证，这里只钉住替身收到的字节）。
        Assert.Equal(Em2WordArea, AreaCodeOf(LastRequest(plc.Device, FinsMemoryArea.CommandWrite)));
        Assert.Equal(new[] { (ushort)0x1234 }, plc.Device.Memory.GetWords("E2_", 500, 1));
        Assert.Equal((short)0x1234, Succeeded(await plc.Client.ReadInt16Async("E2_500")));
    }

    [Theory(Timeout = 60000)]
    [InlineData(ReadLimit, ReadLimit)]
    [InlineData(ReadLimit + 1, ReadLimit, 1)]
    public async Task ReadsAboveTheWordLimit_AreSplitAndReassembled(int count, params int[] expectedPackets)
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        ushort[] pattern = Pattern(count);
        plc.Device.Memory.SetWords("D", 0, pattern);

        ushort[] data = Succeeded(await plc.Client.ReadUInt16Async("D0", count));

        Assert.Equal(pattern, data);
        Assert.Equal(expectedPackets, plc.Device.Requests.Select(QuantityOf).ToArray());
    }

    [Theory(Timeout = 60000)]
    [InlineData(WriteLimit, WriteLimit)]
    [InlineData(WriteLimit + 1, WriteLimit, 1)]
    public async Task WritesAboveTheWordLimit_AreSplitAndKeepTheWordNumbering(int count, params int[] expectedPackets)
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        ushort[] pattern = Pattern(count);

        Succeeded(await plc.Client.WriteUInt16Async("D0", pattern));

        // 分包的偏移由内容证明：第二个包的起始字号必须是第一包的字数（钩子里的 address.Number + offset）。
        Assert.Equal(expectedPackets, plc.Device.Requests.Select(QuantityOf).ToArray());
        Assert.Equal(pattern, plc.Device.Memory.GetWords("D", 0, count));
    }

    // 预置与核对用的字序列：取模保证值不重复，分包错位时断言一定失败。
    private static ushort[] Pattern(int count) => Enumerable.Range(0, count).Select(i => (ushort)(i % 251)).ToArray();
}
