using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens.Protocol;
using Junevy.Communication.Tcp;
using static Junevy.Communication.Siemens.Tests.S7RequestFrame;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 客户端钩子（计划 Task 9 第 1、4 条、P4-D11、P4-D12）：请求帧与第 17.1 节的向量逐字节一致；
/// 字访问把偏移与数量乘以 2 后交给同一个字节编码器；写位每包一位，绝对位号 = 编号 × 8 + 位号（缺省 0）+ 包内偏移；
/// 分包上限取自握手协商的 PDU；范围校验在发出任何请求之前完成。
/// 钩子是 Plc 的 protected internal，测试程序集不能直接调用，因此全部经已连接的客户端在替身上验证。
/// </summary>
public sealed class S7ClientHooksTests
{
    [Fact]
    public async Task ReadBytes_SendsTheV_S7_10RequestAndReturnsTheDeviceBytes()
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => ToBytes("5A"));

        CommResult<byte[]> result = await cpu.Client.ReadBytesAsync("M10", 1);

        AssertRequest(V_S7_10, cpu.Requests[0]);
        Assert.Equal(ToBytes("5A"), Succeeded(result));
    }

    [Fact]
    public async Task ReadWords_SendsTheV_S7_5RequestAndKeepsTheMemoryByteOrder()
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => ToBytes("41 20 00 00"));

        CommResult<byte[]> result = await cpu.Client.ReadAsync("DB1.DBD0", 2);

        // P4-D11：S7 的设备内存顺序就是规范化布局，字节原样交出。
        AssertRequest(V_S7_5, cpu.Requests[0]);
        Assert.Equal(ToBytes("41 20 00 00"), Succeeded(result));
    }

    [Fact]
    public async Task ReadWordsAndReadBytes_FallOnTheSameByteRequest()
    {
        // 字钩子只是"偏移与数量 × 2 的字节钩子"（计划 Task 9 第 1 条），两种访问的请求字节完全相同。
        await using var cpu = await S7CannedCpu.StartAsync(quantity => new byte[quantity]);

        Succeeded(await cpu.Client.ReadAsync("DB1.DBB0", 2));
        Succeeded(await cpu.Client.ReadBytesAsync("DB1.DBB0", 4));

        Assert.Equal(2, cpu.Requests.Count);
        AssertRequest(V_S7_5, cpu.Requests[0]);
        AssertRequest(V_S7_5, cpu.Requests[1]);
    }

    [Fact]
    public async Task WriteBytes_SendsTheV_S7_7Request()
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteBytesAsync("DB1.DBB2", ToBytes("12 34")));

        AssertRequest(V_S7_7, cpu.Requests[0]);
    }

    [Fact]
    public async Task WriteWords_SendsTheDerivedWrite_2WordsRequest()
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteAsync("DB1.DBW2", ToBytes("12 34 56 78")));

        AssertRequest(Write_2Words, cpu.Requests[0]);
    }

    [Theory]
    [InlineData(true, V_S7_9)]
    [InlineData(false, WriteBit_Zero)]
    public async Task WriteBool_SendsTheBitRequestAtTheAbsoluteBit(bool value, string vector)
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteBoolAsync("DB1.DBX10.3", value));

        // 位地址字段 0x53 = 10 × 8 + 3（17.1 节说明段）。
        AssertRequest(vector, cpu.Requests[0]);
    }

    [Fact]
    public async Task WriteBits_OneBitPerPacketAndAbsoluteBitNumbersAcrossByteBorders()
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());
        bool[] values = { true, true, true, true, true, false, false, false };

        Succeeded(await cpu.Client.WriteBitsAsync("DB1.DBX10.3", values));

        // P4-D12：一包只写一位。位号 3 起算的第 6 个点已到 0x58，即字节 11 的位号 0。
        Assert.Equal(new[] { 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A }, cpu.Requests.Select(BitAddress).ToArray());
        Assert.Equal(ToBytes("01 01 01 01 01 00 00 00"), cpu.Requests.SelectMany(Payload).ToArray());
        Assert.All(cpu.Requests, request =>
        {
            Assert.Equal(S7VarItem.RequestBit, TransportSize(request));
            Assert.Equal(1, Quantity(request));
            Assert.Equal(S7VarItem.DataBit, DataTransportSize(request));
        });
    }

    [Fact]
    public async Task ReadBytes_AboveThePacketLimit_SplitsAtTheNegotiatedReadLimit()
    {
        // P4-D12：PDU 240 时一包最多 222 字节。替身按请求长度整块回答，本用例只断言客户端算出的分包数量与偏移。
        await using var cpu = await S7CannedCpu.StartAsync(quantity => new byte[quantity]);

        byte[] data = Succeeded(await cpu.Client.ReadBytesAsync("DB1.DBB0", 223));

        Assert.Equal(223, data.Length);
        Assert.Equal(new[] { 222, 1 }, cpu.Requests.Select(Quantity).ToArray());
        Assert.Equal(new[] { 0, 222 }, cpu.Requests.Select(ByteAddress).ToArray());
    }

    [Fact]
    public async Task WriteBytes_AboveThePacketLimit_SplitsAtTheNegotiatedWriteLimit()
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteBytesAsync("DB1.DBB0", new byte[213]));

        Assert.Equal(new[] { 212, 1 }, cpu.Requests.Select(Quantity).ToArray());
        Assert.Equal(new[] { 0, 212 }, cpu.Requests.Select(ByteAddress).ToArray());
    }

    [Fact]
    public async Task ReadWords_AboveThePacketLimit_SplitsAtWholeWords()
    {
        // 读 112 字 = 224 字节，上限 111 字（222 字节），第二包 1 字从字节 222 起。
        await using var cpu = await S7CannedCpu.StartAsync(quantity => new byte[quantity]);

        Succeeded(await cpu.Client.ReadAsync("DB1.DBB0", 112));

        Assert.Equal(new[] { 222, 2 }, cpu.Requests.Select(Quantity).ToArray());
        Assert.Equal(new[] { 0, 222 }, cpu.Requests.Select(ByteAddress).ToArray());
    }

    [Fact]
    public async Task WriteWords_AboveThePacketLimit_SplitsAtWholeWords()
    {
        await using var cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteAsync("DB1.DBB0", new byte[214]));

        Assert.Equal(new[] { 212, 2 }, cpu.Requests.Select(Quantity).ToArray());
        Assert.Equal(new[] { 0, 212 }, cpu.Requests.Select(ByteAddress).ToArray());
    }

    [Theory]
    [InlineData(V_S7_4, 240)]
    [InlineData(Setup_Ack_960, 960)]
    public async Task NegotiatedPduSize_ReportsTheLatestHandshake(string setupAck, int pdu)
    {
        await using var cpu = await S7CannedCpu.StartAsync(quantity => new byte[quantity], setupAck: setupAck);

        Assert.Equal(pdu, cpu.Client.NegotiatedPduSize);
    }

    [Theory]
    [InlineData("DB1.DBB2097151", 1, PlcAccess.Bytes, true)]
    [InlineData("DB1.DBB2097151", 2, PlcAccess.Bytes, false)]
    [InlineData("DB1.DBB2097150", 1, PlcAccess.Words, true)]
    [InlineData("DB1.DBB2097151", 1, PlcAccess.Words, false)]
    [InlineData("M2097151.7", 1, PlcAccess.Bits, true)]
    [InlineData("M2097151.7", 2, PlcAccess.Bits, false)]
    public async Task ValidateRange_AllowsUpToTheLastBitAddressFieldByte(string address, int count, PlcAccess access, bool legal)
    {
        using S7Client client = S7Client.Create(Transport(), new S7ClientOptions());

        CommResult result = await OperateAsync(client, address, count, access);

        // 合法时通过范围校验，未连接才失败；越界在发出任何请求之前失败。
        Assert.Equal(legal ? CommErrorKind.NotConnected : CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    /// <summary>按访问粒度发一个只依赖入口校验的操作：越界时在校验处失败，合法时走到连接检查。</summary>
    private static async Task<CommResult> OperateAsync(S7Client client, string address, int count, PlcAccess access)
    {
        switch (access)
        {
            case PlcAccess.Bytes:
                return (await client.ReadBytesAsync(address, count)).ToResult();
            case PlcAccess.Words:
                return (await client.ReadAsync(address, count)).ToResult();

            // 位访问只有写入会走到 ValidateRange(Bits)：S7 的位读取由套件改成按字节读（P4-D6）。
            default:
                return await client.WriteBitsAsync(address, new bool[count]);
        }
    }

    // 17.1 节的向量写死了引用号，实际请求用的是进程级计数器的号，比较前先替换。
    private static void AssertRequest(string vector, byte[] request)
        => Assert.Equal(WithReference(vector, ReferenceOf(request)), request);

    // 范围校验在未连接的客户端上进行：端口为 0 时 TcpClientChannel 在构造阶段就拒绝，因此用 S7 的标准端口 102。
    private static TcpClientChannelConfig Transport() => new TcpClientChannelConfig { Host = "127.0.0.1", Port = 102 };
}
