using System.Buffers;
using System.Runtime.CompilerServices;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;
using Junevy.Communication.DebugAssistant.Simulators.Siemens;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>
/// S7 CPU 的黄金向量（计划 Task 13 第 2 条，P4 计划第 17.1 节 V-S7-1 至 V-S7-12）：COTP CR → CC、Setup Communication、
/// Read Var 与 Write Var 的字节，PUT/GET 关闭时的头部错误，数据块不存在与越界的返回码，以及 PDU 的协商。
/// </summary>
public sealed class S7CpuTests
{
    private static readonly byte[] ConnectRequest = Hex("03 00 00 16 11 E0 00 00 00 01 00 C0 01 0A C1 02 01 00 C2 02 01 01");
    private static readonly byte[] SetupRequest = Hex("03 00 00 19 02 F0 80 32 01 00 00 00 01 00 08 00 00 F0 00 00 01 00 01 03 C0");
    private static readonly byte[] ReadDbd0 = Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 02 00 0E 00 00 04 01 12 0A 10 02 00 04 00 01 84 00 00 00");
    private static readonly byte[] WriteDbw2 = Hex("03 00 00 25 02 F0 80 32 01 00 00 00 03 00 0E 00 06 05 01 12 0A 10 02 00 02 00 01 84 00 00 10 00 04 00 10 12 34");
    private static readonly byte[] WriteDbx10Bit3 = Hex("03 00 00 24 02 F0 80 32 01 00 00 00 04 00 0E 00 05 05 01 12 0A 10 01 00 01 00 01 84 00 00 53 00 03 00 01 01");
    private static readonly byte[] ReadMb10 = Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 05 00 0E 00 00 04 01 12 0A 10 02 00 01 00 00 83 00 00 50");
    private static readonly byte[] ReadMissingDb = Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 06 00 0E 00 00 04 01 12 0A 10 02 00 04 00 09 84 00 00 00");
    private static readonly byte[] ReadPutGet = Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 07 00 0E 00 00 04 01 12 0A 10 02 00 04 00 01 84 00 00 00");

    [Fact]
    public void V_S7_1_ConnectRequest_IsConfirmedWithD0()
    {
        var (_, engine) = Create(new S7CpuOptions());

        SimulatorReply reply = Send(engine, ConnectRequest);

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
        Assert.Equal(0xD0, reply.Response![5]);
        Assert.Equal(0x16, reply.Response[3]);
        Assert.Equal(ConnectRequest[^8..], reply.Response[^8..]);
    }

    [Fact]
    public void V_S7_3_SetupCommunication_NegotiatesTheSmallerPduAndAnswersTheVector()
    {
        var (_, engine) = Create(new S7CpuOptions(PduSize: 240));
        Send(engine, ConnectRequest);

        SimulatorReply reply = Send(engine, SetupRequest);

        Assert.Equal(Hex("03 00 00 1B 02 F0 80 32 03 00 00 00 01 00 08 00 00 00 00 F0 00 00 01 00 01 00 F0"), reply.Response);
    }

    [Fact]
    public void Setup_WithAn960Cpu_NegotiatesTheRequestedSize()
    {
        var (_, engine) = Create(new S7CpuOptions(PduSize: 960));
        Send(engine, ConnectRequest);

        SimulatorReply reply = Send(engine, SetupRequest);

        Assert.Equal(new byte[] { 0x03, 0xC0 }, reply.Response![^2..]);
    }

    [Fact]
    public void V_S7_5_ReadVar_ReturnsTheFourBytesOfTheDataBlock()
    {
        var (memory, engine) = Create(new S7CpuOptions());
        Preset(memory, "DB1", 0, Hex("41 20 00 00"));
        Connect(engine);

        SimulatorReply reply = Send(engine, ReadDbd0);

        Assert.Equal(Hex("03 00 00 1D 02 F0 80 32 03 00 00 00 02 00 02 00 08 00 00 04 01 FF 04 00 20 41 20 00 00"), reply.Response);
    }

    [Fact]
    public void V_S7_7_WriteVar_StoresTheWordAndAnswersTheVector()
    {
        var (memory, engine) = Create(new S7CpuOptions());
        Preset(memory, "DB1", 0, new byte[4]);
        Connect(engine);

        SimulatorReply reply = Send(engine, WriteDbw2);

        Assert.Equal(Hex("03 00 00 16 02 F0 80 32 03 00 00 00 03 00 02 00 01 00 00 05 01 FF"), reply.Response);
        Assert.Equal(Hex("12 34"), memory.FindBytes("DB1")!.Read(2, 2));
    }

    [Fact]
    public void V_S7_9_WriteBit_SetsTheBitOfTheByte()
    {
        var (memory, engine) = Create(new S7CpuOptions());
        Preset(memory, "DB1", 0, new byte[16]);
        Connect(engine);

        SimulatorReply reply = Send(engine, WriteDbx10Bit3);

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
        Assert.Equal((byte)0x08, memory.FindBytes("DB1")!.Read(10, 1)[0]);
    }

    [Fact]
    public void V_S7_10_ReadByte_OfTheMerkers_ReturnsTheByte()
    {
        var (memory, engine) = Create(new S7CpuOptions());
        Preset(memory, "M", 10, [0x5A]);
        Connect(engine);

        SimulatorReply reply = Send(engine, ReadMb10);

        Assert.Equal(Hex("03 00 00 1A 02 F0 80 32 03 00 00 00 05 00 02 00 05 00 00 04 01 FF 04 00 08 5A"), reply.Response);
    }

    [Fact]
    public void ReadBit_ReturnsTheBitAsOneValueWithBitTransport()
    {
        var (memory, engine) = Create(new S7CpuOptions());
        Preset(memory, "DB1", 0, new byte[16]);
        memory.FindBytes("DB1")!.Write(10, [0x08]);
        Connect(engine);
        // DB1.DBX10.3 为 1：项的传输尺寸为位（01），应答的数据传输尺寸为位（03），长度 1 位。
        byte[] request = Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 0A 00 0E 00 00 04 01 12 0A 10 01 00 01 00 01 84 00 00 53");

        SimulatorReply reply = Send(engine, request);

        Assert.Equal(Hex("FF 03 00 01 01"), reply.Response![^5..]);
    }

    [Fact]
    public void MissingDataBlock_IsItemReturnCode0A()
    {
        var (_, engine) = Create(new S7CpuOptions());
        Connect(engine);

        SimulatorReply reply = Send(engine, ReadMissingDb);

        Assert.Equal(Hex("03 00 00 19 02 F0 80 32 03 00 00 00 06 00 02 00 04 00 00 04 01 0A 00 00 00"), reply.Response);
    }

    [Fact]
    public void ReadBeyondTheEndOfTheArea_IsItemReturnCode05()
    {
        var (memory, engine) = Create(new S7CpuOptions());
        memory.AddBytes("DB1", 8);
        Connect(engine);
        // DB1.DBB8 起的 4 个字节，超出 8 字节的数据块。
        byte[] request = Hex("03 00 00 1F 02 F0 80 32 01 00 00 00 08 00 0E 00 00 04 01 12 0A 10 02 00 04 00 01 84 00 00 40");

        SimulatorReply reply = Send(engine, request);

        Assert.Equal(new byte[] { 0x05, 0x00, 0x00, 0x00 }, reply.Response![^4..]);
    }

    [Fact]
    public void V_S7_12_PutGetDisabled_IsHeaderError0x81_0x04()
    {
        var (_, engine) = Create(new S7CpuOptions(PutGetEnabled: false));
        Connect(engine);

        SimulatorReply reply = Send(engine, ReadPutGet);

        Assert.Equal(Hex("03 00 00 13 02 F0 80 32 03 00 00 00 07 00 00 00 00 81 04"), reply.Response);
    }

    [Fact]
    public void Requests_BeforeTheConnection_AreIgnored()
    {
        var (_, engine) = Create(new S7CpuOptions());

        SimulatorReply reply = Send(engine, SetupRequest);

        Assert.Equal(SimulatorReplyKind.NoReply, reply.Kind);
    }

    [Fact]
    public void Tcp_UsesTheTpktLengthField_AndUdpHasNoCodec()
    {
        var (_, engine) = Create(new S7CpuOptions());

        Assert.NotNull(engine.CreateRequestCodec(SimulatorTransport.Tcp));
        Assert.Null(engine.CreateRequestCodec(SimulatorTransport.Udp));
        ReadOnlySequence<byte> buffer = new(ReadDbd0);
        Assert.True(engine.CreateRequestCodec(SimulatorTransport.Tcp)!.CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(ReadDbd0, frame.ToArray());
    }

    [Fact]
    public void UnsupportedPduSize_IsRejected()
    {
        var memory = new SimulatorMemory();

        Assert.Throws<ArgumentOutOfRangeException>(() => new S7CpuEngine(memory, new S7CpuOptions(PduSize: 512)));
    }

    private static (SimulatorMemory Memory, S7CpuEngine Engine) Create(S7CpuOptions options)
    {
        var memory = new SimulatorMemory();
        return (memory, new S7CpuEngine(memory, options));
    }

    private static void Connect(S7CpuEngine engine)
    {
        Send(engine, ConnectRequest);
        Send(engine, SetupRequest);
    }

    /// <summary>每个引擎（即每个测试）有自己的会话，COTP 与 Setup 的进度不会串到其他测试里。</summary>
    private static readonly ConditionalWeakTable<S7CpuEngine, SimulatorConnection> Sessions = new();

    private static SimulatorReply Send(S7CpuEngine engine, byte[] request)
    {
        SimulatorConnection connection = Sessions.GetValue(engine, _ => new SimulatorConnection(1, null, new Dictionary<string, object?>()));
        return engine.Handle(request, connection, FaultPlan.None);
    }

    private static void Preset(SimulatorMemory memory, string area, int start, byte[] values)
    {
        ByteArea bytes = memory.FindBytes(area) ?? memory.AddBytes(area, 256);
        bytes.Write(start, values);
    }

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty));
}
