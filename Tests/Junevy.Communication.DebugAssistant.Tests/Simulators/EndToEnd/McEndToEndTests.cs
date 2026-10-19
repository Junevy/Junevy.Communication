using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Melsec;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Melsec;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators.EndToEnd;

/// <summary>
/// MC 模拟器与库的 MC 客户端的端到端矩阵（计划第 8.3 节）。组合：TCP 与 UDP 上的 3E、4E（二进制或 ASCII），以及串口上的 4C
/// 格式 5（二进制）与格式 4（ASCII），各带和校验与不带和校验。每个组合跑六个场景；拆包使用 1000 个字，超过 3E 与 4E 的单个请求上限。
/// 测试名的组合标识与第 33.3 节的矩阵一致。
/// </summary>
[Collection(SimulatorEndToEndCollection.Name)]
public sealed class McEndToEndTests
{
    private const int RequestTimeoutMilliseconds = 2000;
    private const int SilentTimeoutMilliseconds = 300;

    private static readonly string[] NetworkCombinations =
    [
        "tcp-3e-binary", "tcp-3e-ascii", "tcp-4e-binary", "tcp-4e-ascii",
        "udp-3e-binary", "udp-3e-ascii", "udp-4e-binary", "udp-4e-ascii",
    ];

    private static readonly string[] SerialCombinations =
    [
        "serial-4c-format5", "serial-4c-format5-nosum", "serial-4c-format4", "serial-4c-format4-nosum",
    ];

    [Theory]
    [MemberData(nameof(NetworkCombinationData))]
    public Task ReadWriteRoundTrip(string combination) => ReadWriteRoundTripAsync(combination);

    [SerialPairTheory]
    [MemberData(nameof(SerialCombinationData))]
    public Task ReadWriteRoundTrip_Serial(string combination) => ReadWriteRoundTripAsync(combination);

    [Theory]
    [MemberData(nameof(NetworkCombinationData))]
    public Task WriteBitsRoundTrip(string combination) => WriteBitsRoundTripAsync(combination);

    [SerialPairTheory]
    [MemberData(nameof(SerialCombinationData))]
    public Task WriteBitsRoundTrip_Serial(string combination) => WriteBitsRoundTripAsync(combination);

    [Theory]
    [MemberData(nameof(NetworkCombinationData))]
    public Task SplitRead(string combination) => SplitReadAsync(combination);

    [SerialPairTheory]
    [MemberData(nameof(SerialCombinationData))]
    public Task SplitRead_Serial(string combination) => SplitReadAsync(combination);

    [Theory]
    [MemberData(nameof(NetworkCombinationData))]
    public Task ForcedErrorIsRemoteError(string combination) => ForcedErrorIsRemoteErrorAsync(combination);

    [SerialPairTheory]
    [MemberData(nameof(SerialCombinationData))]
    public Task ForcedErrorIsRemoteError_Serial(string combination) => ForcedErrorIsRemoteErrorAsync(combination);

    [Theory]
    [MemberData(nameof(NetworkCombinationData))]
    public Task NoReplyTimesOut(string combination) => NoReplyTimesOutAsync(combination);

    [SerialPairTheory]
    [MemberData(nameof(SerialCombinationData))]
    public Task NoReplyTimesOut_Serial(string combination) => NoReplyTimesOutAsync(combination);

    [Theory]
    [MemberData(nameof(NetworkCombinationData))]
    public Task DisconnectThenReconnect(string combination) => DisconnectThenReconnectAsync(combination);

    [SerialPairTheory]
    [MemberData(nameof(SerialCombinationData))]
    public Task DisconnectThenReconnect_Serial(string combination) => DisconnectThenReconnectAsync(combination);

    public static IEnumerable<object[]> NetworkCombinationData() => NetworkCombinations.Select(combination => new object[] { combination });

    public static IEnumerable<object[]> SerialCombinationData() => SerialCombinations.Select(combination => new object[] { combination });

    private static async Task ReadWriteRoundTripAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using McClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        ushort[] values = [0x1234, 0xBEEF];

        CommAssert.Succeeded(await client.WriteUInt16Async("D100", values));

        CommResult<ushort[]> read = await client.ReadUInt16Async("D100", values.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(values, read.Data);
        Assert.Equal(values, rig.Memory.FindWords("D")!.Read(100, values.Length));
    }

    private static async Task WriteBitsRoundTripAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using McClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        bool[] bits = [true, false, true, true];

        CommAssert.Succeeded(await client.WriteBitsAsync("M10", bits));

        CommResult<bool[]> read = await client.ReadBitsAsync("M10", bits.Length);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(bits, read.Data);
        Assert.Equal(bits, rig.Memory.FindBits("M")!.Read(10, bits.Length));
    }

    private static async Task SplitReadAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using McClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        ushort[] seeded = Enumerable.Range(0, 1000).Select(index => (ushort)(index * 7 + 3)).ToArray();
        rig.Memory.FindWords("D")!.Write(0, seeded);
        int before = rig.RequestCount;

        CommResult<ushort[]> read = await client.ReadUInt16Async("D0", seeded.Length);

        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(seeded, read.Data);
        Assert.True(rig.RequestCount - before >= 2, "a read beyond one MC request must be split into several requests");
    }

    private static async Task ForcedErrorIsRemoteErrorAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using McClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { ForcedErrorCode = 0xC051 };

        CommResult<ushort[]> failed = await client.ReadUInt16Async("D100", 1);

        Assert.False(failed.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, failed.ErrorKind);
        Assert.Equal(0xC051L, failed.ProtocolErrorCode);
    }

    private static async Task NoReplyTimesOutAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using McClient client = await ConnectAsync(rig, combination, SilentTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { NoReply = true };

        CommResult<ushort[]> silent = await client.ReadUInt16Async("D100", 1);

        Assert.False(silent.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, silent.ErrorKind);
    }

    private static async Task DisconnectThenReconnectAsync(string combination)
    {
        await using SimulatorRig rig = await StartAsync(combination);
        await using McClient client = await ConnectAsync(rig, combination, RequestTimeoutMilliseconds);
        rig.Faults.Plan = new FaultPlan { DisconnectOnNextRequest = true };

        CommResult<ushort[]> cut = await client.ReadUInt16Async("D100", 1);
        Assert.False(cut.IsSuccess);

        await client.DisconnectAsync();
        CommAssert.Succeeded(await client.ConnectAsync());
        CommAssert.Succeeded(await client.ReadUInt16Async("D100", 1));
    }

    private static (SimulatorTransport Transport, McFrameType Frame, McEncoding Encoding, bool SumCheck) Parse(string combination) => combination switch
    {
        "tcp-3e-binary" => (SimulatorTransport.Tcp, McFrameType.Frame3E, McEncoding.Binary, false),
        "tcp-3e-ascii" => (SimulatorTransport.Tcp, McFrameType.Frame3E, McEncoding.Ascii, false),
        "tcp-4e-binary" => (SimulatorTransport.Tcp, McFrameType.Frame4E, McEncoding.Binary, false),
        "tcp-4e-ascii" => (SimulatorTransport.Tcp, McFrameType.Frame4E, McEncoding.Ascii, false),
        "udp-3e-binary" => (SimulatorTransport.Udp, McFrameType.Frame3E, McEncoding.Binary, false),
        "udp-3e-ascii" => (SimulatorTransport.Udp, McFrameType.Frame3E, McEncoding.Ascii, false),
        "udp-4e-binary" => (SimulatorTransport.Udp, McFrameType.Frame4E, McEncoding.Binary, false),
        "udp-4e-ascii" => (SimulatorTransport.Udp, McFrameType.Frame4E, McEncoding.Ascii, false),
        "serial-4c-format5" => (SimulatorTransport.Serial, McFrameType.Frame4C, McEncoding.Binary, true),
        "serial-4c-format5-nosum" => (SimulatorTransport.Serial, McFrameType.Frame4C, McEncoding.Binary, false),
        "serial-4c-format4" => (SimulatorTransport.Serial, McFrameType.Frame4C, McEncoding.Ascii, true),
        "serial-4c-format4-nosum" => (SimulatorTransport.Serial, McFrameType.Frame4C, McEncoding.Ascii, false),
        _ => throw new ArgumentOutOfRangeException(nameof(combination), combination, "unknown MC combination"),
    };

    private static Task<SimulatorRig> StartAsync(string combination)
    {
        (SimulatorTransport transport, McFrameType frame, McEncoding encoding, bool sumCheck) = Parse(combination);
        McFrameKind simulatedFrame = frame switch
        {
            McFrameType.Frame4E => McFrameKind.Frame4E,
            McFrameType.Frame4C => McFrameKind.Frame4C,
            _ => McFrameKind.Frame3E,
        };
        McEncodingKind simulatedEncoding = encoding == McEncoding.Ascii ? McEncodingKind.Ascii : McEncodingKind.Binary;
        return SimulatorRig.StartAsync(transport, memory => new McSlaveEngine(memory, new McSlaveOptions(simulatedFrame, simulatedEncoding, sumCheck)));
    }

    private static async Task<McClient> ConnectAsync(SimulatorRig rig, string combination, int requestTimeoutMilliseconds)
    {
        (_, McFrameType frame, McEncoding encoding, bool sumCheck) = Parse(combination);
        var client = McClient.Create(rig.ClientConfig(requestTimeoutMilliseconds), new McClientOptions { FrameType = frame, Encoding = encoding, SumCheck = sumCheck });
        CommAssert.Succeeded(await client.ConnectAsync());
        return client;
    }
}
