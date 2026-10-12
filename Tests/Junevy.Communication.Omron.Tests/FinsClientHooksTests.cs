using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 客户端钩子（计划 Task 13 第 3 条、P4-D6、P4-D11、P4-D12、P4-D18）：请求帧与第 17.2 节的向量逐字节一致；
/// 字访问用字区代码、位写用位区代码并按"字号 × 16 + 位号 + 偏移"连续排布；FINS 没有位软元件，位读取由套件改为读覆盖的字；
/// 单包上限是固定值（读 999、写 996），范围校验在发出任何请求之前完成；路由的节点号取自最近一次握手。
/// 钩子是 Plc 的 protected internal，测试程序集不能直接调用，因此全部经已连接的客户端在替身上验证。
/// </summary>
public sealed class FinsClientHooksTests
{
    [Fact]
    public async Task ReadWords_SendsTheV_F_3RequestAndKeepsTheMemoryByteOrder()
    {
        await using var plc = await FinsCannedPlc.StartAsync(_ => ToBytes("12 34 56 78"));

        CommResult<byte[]> result = await plc.Client.ReadAsync("D100", 2);

        AssertRequest(V_F_3, plc.Requests[0]);
        Assert.Equal(ToBytes("12 34 56 78"), Succeeded(result));

        // P4-D18：DA1 取握手得到的服务端节点号。
        Assert.Equal(ServerNode, plc.Requests[0][DestinationNodeIndex]);
    }

    [Fact]
    public async Task ReadWordsAndReadBytes_FallOnTheSameWordRequest()
    {
        // FINS 的软元件全是字单位，套件的字节访问按整字路由（P4-D4），因此两种入口的请求字节完全相同。
        await using var plc = await FinsCannedPlc.StartAsync(quantity => new byte[quantity]);

        Succeeded(await plc.Client.ReadAsync("D100", 2));
        Succeeded(await plc.Client.ReadBytesAsync("D100", 4));

        Assert.Equal(2, plc.Requests.Count);
        AssertRequest(V_F_3, plc.Requests[0]);
        AssertRequest(V_F_3, plc.Requests[1]);
    }

    [Fact]
    public async Task WriteWords_SendsTheV_F_5Request()
    {
        await using var plc = await FinsCannedPlc.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await plc.Client.WriteAsync("D200", ToBytes("00 FF")));

        AssertRequest(V_F_5_Request, plc.Requests[0]);
    }

    [Theory]
    [InlineData(true, V_F_7)]
    [InlineData(false, WriteBit_Zero)]
    public async Task WriteBool_SendsTheBitRequestWithTheBitAreaCode(bool value, string vector)
    {
        await using var plc = await FinsCannedPlc.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await plc.Client.WriteBoolAsync("W10.03", value));

        // 17.3 节：W 区的字代码 0xB1、位代码 0x31；位写用后者。
        AssertRequest(vector, plc.Requests[0]);
        Assert.Equal(WBitArea, plc.Requests[0][AreaCodeIndex]);
    }

    [Fact]
    public async Task WriteBits_StayInOnePacketWithOneBytePerPoint()
    {
        await using var plc = await FinsCannedPlc.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await plc.Client.WriteBitsAsync("W10.03", new[] { true, false, true }));

        AssertRequest(WriteBits_3Points, plc.Requests[0]);
    }

    [Fact]
    public async Task WriteBits_DoesNotAskForReadModifyWrite()
    {
        // CanWriteBitsNatively 返回 true：设备自己处理"字里的位"，套件不再读-改-写（P4-D6）。
        await using var plc = await FinsCannedPlc.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await plc.Client.WriteBitsAsync("W10.03", new[] { true }));

        Assert.Single(plc.Requests);
        Assert.Equal(1, QuantityOf(plc.Requests[0]));
    }

    [Fact]
    public async Task ReadBits_ReadsTheCoveringWordsWithTheWordAreaCode()
    {
        // FINS 没有位软元件：位读取由套件改成读覆盖的字（P4-D6）。D100 位号 2 起的 20 个点占 2 个字。
        await using var plc = await FinsCannedPlc.StartAsync(quantity => new byte[quantity]);

        bool[] bits = Succeeded(await plc.Client.ReadBitsAsync("D100.02", 20));

        Assert.Equal(20, bits.Length);
        Assert.Equal(DWordArea, plc.Requests[0][AreaCodeIndex]);
        Assert.Equal(100, WordAddressOf(plc.Requests[0]));
        Assert.Equal(2, QuantityOf(plc.Requests[0]));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public async Task ReadWords_SplitsAtThe999WordLimit(int words)
    {
        // P4-D12：读上限 999 字，替身按请求长度整块回答，本用例只断言分包数量与偏移。
        await using var plc = await FinsCannedPlc.StartAsync(quantity => new byte[quantity]);

        byte[] data = Succeeded(await plc.Client.ReadAsync("D100", words));

        Assert.Equal(words * 2, data.Length);
        Assert.Equal(words == 999 ? new[] { 999 } : new[] { 999, 1 }, plc.Requests.Select(QuantityOf).ToArray());
        Assert.Equal(words == 999 ? new[] { 100 } : new[] { 100, 1099 }, plc.Requests.Select(WordAddressOf).ToArray());
    }

    [Theory]
    [InlineData(996)]
    [InlineData(997)]
    public async Task WriteWords_SplitAtThe996WordLimit(int words)
    {
        // P4-D12：写上限 996 字。
        await using var plc = await FinsCannedPlc.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await plc.Client.WriteAsync("D200", new byte[words * 2]));

        Assert.Equal(words == 996 ? new[] { 996 } : new[] { 996, 1 }, plc.Requests.Select(QuantityOf).ToArray());
        Assert.Equal(words == 996 ? new[] { 200 } : new[] { 200, 1196 }, plc.Requests.Select(WordAddressOf).ToArray());
    }

    [Fact]
    public async Task WriteBits_SplitAtThe996PointLimitAndKeepNumberingContinuousAcrossWords()
    {
        // P4-D6：绝对位号 = 字号 × 16 + 位号 + 包内偏移；第 997 个点（0 起算的 996）落在第 62 字的位号 4。
        await using var plc = await FinsCannedPlc.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await plc.Client.WriteBitsAsync("D0.00", new bool[997]));

        Assert.Equal(new[] { 996, 1 }, plc.Requests.Select(QuantityOf).ToArray());
        Assert.Equal(new[] { 0, 62 }, plc.Requests.Select(WordAddressOf).ToArray());
        Assert.Equal(new[] { 0, 4 }, plc.Requests.Select(BitNumberOf).ToArray());
    }

    [Fact]
    public async Task Requests_AreNumberedByAMonotonicSessionId()
    {
        // P4-D17：SID 取自进程级计数器，与心跳探测共用，因此同一通道上不会撞号。
        await using var plc = await FinsCannedPlc.StartAsync(quantity => new byte[quantity]);

        Succeeded(await plc.Client.ReadAsync("D100", 1));
        Succeeded(await plc.Client.ReadAsync("D100", 1));

        Assert.Equal(2, plc.Requests.Count);
        Assert.NotEqual(SessionIdOf(plc.Requests[0]), SessionIdOf(plc.Requests[1]));
    }

    [Fact]
    public async Task ClientNodeAndServerNode_ReportTheLatestHandshake()
    {
        await using var plc = await FinsCannedPlc.StartAsync(quantity => new byte[quantity]);

        Assert.Equal(ClientNode, plc.Client.ClientNode);
        Assert.Equal(ServerNode, plc.Client.ServerNode);
    }

    [Fact]
    public async Task Reconnect_TakesTheNewClientNodeAndFollowingRequestsUseItAsSource()
    {
        // P4-D18、计划 Task 13 第 5 条：重连后 PLC 可能另派节点号，后续请求的 SA1 跟着变。
        await using var plc = await FinsCannedPlc.StartAsync(
            quantity => new byte[quantity],
            assignClientNode: round => round == 1 ? ClientNode : ReassignedClientNode);

        Succeeded(await plc.Client.ReadAsync("D100", 1));
        Assert.Equal(ClientNode, plc.Requests[0][SourceNodeIndex]);

        await plc.Client.DisconnectAsync();
        Succeeded(await plc.Client.ConnectAsync());
        Succeeded(await plc.Client.ReadAsync("D100", 1));

        Assert.Equal(ReassignedClientNode, plc.Client.ClientNode!.Value);
        Assert.Equal(ReassignedClientNode, plc.Requests[1][SourceNodeIndex]);
        Assert.Equal(ServerNode, plc.Requests[1][DestinationNodeIndex]);
    }

    [Theory]
    [InlineData("D65535", 1, PlcAccess.Words, true)]
    [InlineData("D65534", 2, PlcAccess.Words, true)]
    [InlineData("D65535", 2, PlcAccess.Words, false)]
    [InlineData("W0.00", 16, PlcAccess.Bits, true)]
    [InlineData("D65535.15", 1, PlcAccess.Bits, true)]
    [InlineData("D65535.15", 2, PlcAccess.Bits, false)]
    public async Task ValidateRange_AcceptsEveryOperationThatEndsInsideWord65535(string address, int count, PlcAccess access, bool legal)
    {
        // 17.2 节：字地址字段是 2 字节；越过末尾字号在发出任何请求之前返回 InvalidRequest（P4-D16）。
        using FinsClient client = FinsClient.Create(Transport(), new FinsClientOptions());

        CommResult result = await OperateAsync(client, address, count, access);

        Assert.Equal(legal ? CommErrorKind.NotConnected : CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    /// <summary>按访问粒度发一个只依赖入口校验的操作：越界时在校验处失败，合法时走到连接检查。</summary>
    private static async Task<CommResult> OperateAsync(FinsClient client, string address, int count, PlcAccess access)
        => access == PlcAccess.Words
            ? (await client.ReadAsync(address, count)).ToResult()
            : await client.WriteBitsAsync(address, new bool[count]);

    // 第二次握手派给客户端的节点号（替身的 assignClientNode 给出）。
    private const byte ReassignedClientNode = 0x0C;

    // 17.2 节的向量写死了 SID，实际请求用的是进程级计数器的号，比较前先替换。
    private static void AssertRequest(string vector, byte[] request)
        => Assert.Equal(WithSessionId(vector, SessionIdOf(request)), request);

    // 端口为 0 的 TCP 配置在创建通道时就被 TcpClientChannel 拒绝，因此统一用 FINS 的标准端口 9600。
    private static TcpClientChannelConfig Transport() => LoopbackTcp.Client(9600);
}
