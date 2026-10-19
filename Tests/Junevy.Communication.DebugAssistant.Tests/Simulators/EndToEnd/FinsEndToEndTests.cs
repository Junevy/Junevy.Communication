using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Omron;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Omron;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators.EndToEnd;

/// <summary>
/// FINS 模拟器与库的 FINS 客户端的端到端矩阵（计划第 8.3 节）。组合只有 FINS/TCP。客户端节点由握手分配（模拟器分配 0x0B）；
/// 位写入使用 <c>D10.03</c> 形式的地址；拆包使用 1000 个字，超过 FINS 的单个读请求上限。
/// </summary>
[Collection(SimulatorEndToEndCollection.Name)]
public sealed class FinsEndToEndTests
{
    private const int RequestTimeoutMilliseconds = 2000;
    private const int SilentTimeoutMilliseconds = 300;

    [Fact]
    public async Task ReadWriteRoundTrip()
    {
        await using SimulatorRig rig = await StartAsync();
        await using FinsClient client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        ushort[] values = [0x1234, 0xBEEF];

        Assert.True((await client.WriteUInt16Async("D100", values)).IsSuccess);

        CommResult<ushort[]> read = await client.ReadUInt16Async("D100", values.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(values, read.Data);
        Assert.Equal(values, rig.Memory.FindWords("D")!.Read(100, values.Length));
    }

    [Fact]
    public async Task WriteBitsRoundTrip()
    {
        await using SimulatorRig rig = await StartAsync();
        await using FinsClient client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        bool[] bits = [true, false, true];

        Assert.True((await client.WriteBitsAsync("D10.03", bits)).IsSuccess);

        CommResult<bool[]> read = await client.ReadBitsAsync("D10.03", bits.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(bits, read.Data);
    }

    [Fact]
    public async Task SplitRead()
    {
        await using SimulatorRig rig = await StartAsync();
        await using FinsClient client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        ushort[] seeded = Enumerable.Range(0, 1000).Select(index => (ushort)(index * 11 + 5)).ToArray();
        rig.Memory.FindWords("D")!.Write(0, seeded);
        int before = rig.RequestCount;

        CommResult<ushort[]> read = await client.ReadUInt16Async("D0", seeded.Length);

        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(seeded, read.Data);
        Assert.True(rig.RequestCount - before >= 2, "a read beyond one FINS request must be split into several requests");
    }

    [Fact]
    public async Task ForcedErrorIsRemoteError()
    {
        await using SimulatorRig rig = await StartAsync();
        await using FinsClient client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { ForcedErrorCode = 0x1103 };

        CommResult<ushort[]> failed = await client.ReadUInt16Async("D100", 1);

        Assert.False(failed.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, failed.ErrorKind);
        Assert.Equal(0x1103L, failed.ProtocolErrorCode);
    }

    [Fact]
    public async Task NoReplyTimesOut()
    {
        await using SimulatorRig rig = await StartAsync();
        await using FinsClient client = await ConnectAsync(rig, SilentTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { NoReply = true };

        CommResult<ushort[]> silent = await client.ReadUInt16Async("D100", 1);

        Assert.False(silent.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, silent.ErrorKind);
    }

    [Fact]
    public async Task DisconnectThenReconnect()
    {
        await using SimulatorRig rig = await StartAsync();
        await using FinsClient client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { DisconnectOnNextRequest = true };

        CommResult<ushort[]> cut = await client.ReadUInt16Async("D100", 1);
        Assert.False(cut.IsSuccess);

        await client.DisconnectAsync();
        Assert.True((await client.ConnectAsync()).IsSuccess, "the client must reconnect");
        Assert.True((await client.ReadUInt16Async("D100", 1)).IsSuccess);
    }

    private static Task<SimulatorRig> StartAsync() => SimulatorRig.StartAsync(
        SimulatorTransport.Tcp,
        memory => new FinsSlaveEngine(memory, new FinsSlaveOptions(1, 0x0B)));

    private static async Task<FinsClient> ConnectAsync(SimulatorRig rig, int requestTimeoutMilliseconds)
    {
        var client = FinsClient.Create(rig.ClientConfig(requestTimeoutMilliseconds), new FinsClientOptions());
        Assert.True((await client.ConnectAsync()).IsSuccess, "the client must connect");
        return client;
    }
}
