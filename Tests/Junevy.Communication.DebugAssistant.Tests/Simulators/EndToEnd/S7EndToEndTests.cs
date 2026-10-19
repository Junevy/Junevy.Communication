using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Siemens;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators.EndToEnd;

/// <summary>
/// S7 CPU 模拟器与库的 S7 客户端的端到端矩阵（计划第 8.3 节）。组合只有 ISO-on-TCP。数据块 DB1 由测试在启动前创建；
/// 拆包使用 300 字节，超过协商后的 PDU（240）的单次读上限。
/// </summary>
[Collection(SimulatorEndToEndCollection.Name)]
public sealed class S7EndToEndTests
{
    private const int RequestTimeoutMilliseconds = 2000;
    private const int SilentTimeoutMilliseconds = 300;

    [Fact]
    public async Task ReadWriteRoundTrip()
    {
        await using SimulatorRig rig = await StartAsync();
        await using S7Client client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        ushort[] values = [0x1234, 0xBEEF];

        Assert.True((await client.WriteUInt16Async("DB1.DBW0", values)).IsSuccess);

        CommResult<ushort[]> read = await client.ReadUInt16Async("DB1.DBW0", values.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(values, read.Data);
        Assert.Equal(new byte[] { 0x12, 0x34, 0xBE, 0xEF }, rig.Memory.FindBytes("DB1")!.Read(0, 4));
    }

    [Fact]
    public async Task WriteBitsRoundTrip()
    {
        await using SimulatorRig rig = await StartAsync();
        await using S7Client client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        bool[] bits = [true, false, true];

        Assert.True((await client.WriteBitsAsync("DB1.DBX10.0", bits)).IsSuccess);

        CommResult<bool[]> read = await client.ReadBitsAsync("DB1.DBX10.0", bits.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(bits, read.Data);
    }

    [Fact]
    public async Task SplitRead()
    {
        await using SimulatorRig rig = await StartAsync();
        await using S7Client client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        byte[] seeded = Enumerable.Range(0, 300).Select(index => (byte)(index * 5 + 1)).ToArray();
        rig.Memory.FindBytes("DB1")!.Write(0, seeded);
        int before = rig.RequestCount;

        CommResult<byte[]> read = await client.ReadBytesAsync("DB1.DBB0", seeded.Length);

        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(seeded, read.Data);
        Assert.True(rig.RequestCount - before >= 2, "a read beyond one S7 PDU must be split into several requests");
    }

    [Fact]
    public async Task ForcedErrorIsRemoteError()
    {
        await using SimulatorRig rig = await StartAsync();
        await using S7Client client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { ForcedErrorCode = 0x8504 };

        CommResult<ushort[]> failed = await client.ReadUInt16Async("DB1.DBW0", 1);

        Assert.False(failed.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, failed.ErrorKind);
        Assert.Equal(0x8504L, failed.ProtocolErrorCode);
    }

    [Fact]
    public async Task NoReplyTimesOut()
    {
        await using SimulatorRig rig = await StartAsync();
        await using S7Client client = await ConnectAsync(rig, SilentTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { NoReply = true };

        CommResult<ushort[]> silent = await client.ReadUInt16Async("DB1.DBW0", 1);

        Assert.False(silent.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, silent.ErrorKind);
    }

    [Fact]
    public async Task DisconnectThenReconnect()
    {
        await using SimulatorRig rig = await StartAsync();
        await using S7Client client = await ConnectAsync(rig, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { DisconnectOnNextRequest = true };

        CommResult<ushort[]> cut = await client.ReadUInt16Async("DB1.DBW0", 1);
        Assert.False(cut.IsSuccess);

        await client.DisconnectAsync();
        Assert.True((await client.ConnectAsync()).IsSuccess, "the client must reconnect");
        Assert.True((await client.ReadUInt16Async("DB1.DBW0", 1)).IsSuccess);
    }

    private static Task<SimulatorRig> StartAsync() => SimulatorRig.StartAsync(SimulatorTransport.Tcp, memory =>
    {
        memory.AddBytes("DB1", 1024);
        return new S7CpuEngine(memory, new S7CpuOptions(240, true));
    });

    private static async Task<S7Client> ConnectAsync(SimulatorRig rig, int requestTimeoutMilliseconds)
    {
        var client = S7Client.Create(rig.ClientConfig(requestTimeoutMilliseconds), new S7ClientOptions());
        Assert.True((await client.ConnectAsync()).IsSuccess, "the client must connect");
        return client;
    }
}
