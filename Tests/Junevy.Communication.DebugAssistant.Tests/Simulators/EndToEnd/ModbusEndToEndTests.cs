using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Modbus;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators.EndToEnd;

/// <summary>
/// Modbus 模拟器与库的 Modbus 客户端的端到端矩阵（计划第 8.3 节）。组合：TCP 与 UDP 上的 MBAP、RTU over TCP 与 RTU over UDP，
/// 以及串口上的 RTU。每个组合跑六个场景：读写往返、写位、拆包（超过单个请求的 125 个寄存器）、强制错误码、不应答（超时）、
/// 断开后重连。测试名的组合标识（<c>tcp-mbap</c> 等）与第 33.3 节的矩阵一致。
/// </summary>
[Collection(SimulatorEndToEndCollection.Name)]
public sealed class ModbusEndToEndTests
{
    private const int RequestTimeoutMilliseconds = 2000;
    private const int SilentTimeoutMilliseconds = 300;

    [Theory]
    [InlineData("tcp-mbap")]
    [InlineData("tcp-rtu")]
    [InlineData("udp-mbap")]
    [InlineData("udp-rtu")]
    public Task ReadWriteRoundTrip(string combination) => ReadWriteRoundTripAsync(combination);

    [SerialPairTheory]
    [InlineData("serial-rtu")]
    public Task ReadWriteRoundTrip_Serial(string combination) => ReadWriteRoundTripAsync(combination);

    [Theory]
    [InlineData("tcp-mbap")]
    [InlineData("tcp-rtu")]
    [InlineData("udp-mbap")]
    [InlineData("udp-rtu")]
    public Task WriteBitsRoundTrip(string combination) => WriteBitsRoundTripAsync(combination);

    [SerialPairTheory]
    [InlineData("serial-rtu")]
    public Task WriteBitsRoundTrip_Serial(string combination) => WriteBitsRoundTripAsync(combination);

    [Theory]
    [InlineData("tcp-mbap")]
    [InlineData("tcp-rtu")]
    [InlineData("udp-mbap")]
    [InlineData("udp-rtu")]
    public Task SplitRead(string combination) => SplitReadAsync(combination);

    [SerialPairTheory]
    [InlineData("serial-rtu")]
    public Task SplitRead_Serial(string combination) => SplitReadAsync(combination);

    [Theory]
    [InlineData("tcp-mbap")]
    [InlineData("tcp-rtu")]
    [InlineData("udp-mbap")]
    [InlineData("udp-rtu")]
    public Task ForcedErrorIsRemoteError(string combination) => ForcedErrorIsRemoteErrorAsync(combination);

    [SerialPairTheory]
    [InlineData("serial-rtu")]
    public Task ForcedErrorIsRemoteError_Serial(string combination) => ForcedErrorIsRemoteErrorAsync(combination);

    [Theory]
    [InlineData("tcp-mbap")]
    [InlineData("tcp-rtu")]
    [InlineData("udp-mbap")]
    [InlineData("udp-rtu")]
    public Task NoReplyTimesOut(string combination) => NoReplyTimesOutAsync(combination);

    [SerialPairTheory]
    [InlineData("serial-rtu")]
    public Task NoReplyTimesOut_Serial(string combination) => NoReplyTimesOutAsync(combination);

    [Theory]
    [InlineData("tcp-mbap")]
    [InlineData("tcp-rtu")]
    [InlineData("udp-mbap")]
    [InlineData("udp-rtu")]
    public Task DisconnectThenReconnect(string combination) => DisconnectThenReconnectAsync(combination);

    [SerialPairTheory]
    [InlineData("serial-rtu")]
    public Task DisconnectThenReconnect_Serial(string combination) => DisconnectThenReconnectAsync(combination);

    private static async Task ReadWriteRoundTripAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using ModbusClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        ushort[] values = [0x1234, 0xBEEF];

        Assert.True((await client.WriteUInt16Async("HR100", values)).IsSuccess);

        CommResult<ushort[]> read = await client.ReadUInt16Async("HR100", values.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(values, read.Data);
        Assert.Equal(values, rig.Memory.FindWords("HR")!.Read(100, values.Length));
    }

    private static async Task WriteBitsRoundTripAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using ModbusClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        bool[] bits = [true, false, true];

        Assert.True((await client.WriteBitsAsync("C5", bits)).IsSuccess);

        CommResult<bool[]> read = await client.ReadBitsAsync("C5", bits.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(bits, read.Data);
        Assert.Equal(bits, rig.Memory.FindBits("C")!.Read(5, bits.Length));
    }

    private static async Task SplitReadAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using ModbusClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        ushort[] seeded = Enumerable.Range(0, 130).Select(index => (ushort)(index * 3 + 1)).ToArray();
        rig.Memory.FindWords("HR")!.Write(0, seeded);
        int before = rig.RequestCount;

        CommResult<ushort[]> read = await client.ReadUInt16Async("HR0", seeded.Length);

        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(seeded, read.Data);
        Assert.True(rig.RequestCount - before >= 2, "a read beyond one Modbus request must be split into several requests");
    }

    private static async Task ForcedErrorIsRemoteErrorAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using ModbusClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { ForcedErrorCode = 0x02 };

        CommResult<ushort[]> failed = await client.ReadUInt16Async("HR100", 1);

        Assert.False(failed.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, failed.ErrorKind);
        Assert.Equal(0x02L, failed.ProtocolErrorCode);
    }

    private static async Task NoReplyTimesOutAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using ModbusClient client = await ConnectAsync(rig, combination, SilentTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { NoReply = true };

        CommResult<ushort[]> silent = await client.ReadUInt16Async("HR100", 1);

        Assert.False(silent.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, silent.ErrorKind);
    }

    private static async Task DisconnectThenReconnectAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using ModbusClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { DisconnectOnNextRequest = true };

        CommResult<ushort[]> cut = await client.ReadUInt16Async("HR100", 1);
        Assert.False(cut.IsSuccess);

        await client.DisconnectAsync();
        Assert.True((await client.ConnectAsync()).IsSuccess, "the client must reconnect");
        Assert.True((await client.ReadUInt16Async("HR100", 1)).IsSuccess);
    }

    private static (SimulatorTransport Transport, ModbusFraming Framing) Parse(string combination) => combination switch
    {
        "tcp-mbap" => (SimulatorTransport.Tcp, ModbusFraming.Mbap),
        "tcp-rtu" => (SimulatorTransport.Tcp, ModbusFraming.Rtu),
        "udp-mbap" => (SimulatorTransport.Udp, ModbusFraming.Mbap),
        "udp-rtu" => (SimulatorTransport.Udp, ModbusFraming.Rtu),
        "serial-rtu" => (SimulatorTransport.Serial, ModbusFraming.Rtu),
        _ => throw new ArgumentOutOfRangeException(nameof(combination), combination, "unknown Modbus combination"),
    };

    private static Task<SimulatorRig> StartAsync(string combination)
    {
        (SimulatorTransport transport, ModbusFraming framing) = Parse(combination);
        return SimulatorRig.StartAsync(transport, memory => new ModbusSlaveEngine(
            memory,
            new ModbusSlaveOptions(new byte[] { 1 }, framing == ModbusFraming.Rtu ? ModbusFrameMode.Rtu : ModbusFrameMode.Mbap)));
    }

    private static async Task<ModbusClient> ConnectAsync(SimulatorRig rig, string combination, int requestTimeoutMilliseconds)
    {
        ModbusFraming framing = Parse(combination).Framing;
        var client = ModbusClient.Create(rig.ClientConfig(requestTimeoutMilliseconds), new ModbusClientOptions { Framing = framing, UnitId = 1 });
        Assert.True((await client.ConnectAsync()).IsSuccess, "the client must connect");
        return client;
    }
}
