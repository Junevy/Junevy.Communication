using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;
using Junevy.Communication.DebugAssistant.Simulators.Omron;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>
/// FINS 从站的黄金向量（计划 Task 13 第 2 条，P4 计划第 17.2 节 V-F-1 至 V-F-8）：FINS/TCP 的节点地址请求与应答，
/// 字与位的读写，DA 与 SA 对调，结束码 0x1103，以及目的节点不符时的忽略。
/// </summary>
public sealed class FinsSlaveTests
{
    private static readonly FinsSlaveOptions Options = new(ServerNode: 0x01, AssignedClientNode: 0x0B);

    [Fact]
    public void V_F_1_NodeAddressRequest_AnswersWithTheAssignedClientNode()
    {
        var (_, engine) = Create();

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 00"));

        Assert.Equal(Hex("46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 0B 00 00 00 01"), reply.Response);
    }

    [Fact]
    public void NodeAddressRequest_WithAFixedNode_EchoesIt()
    {
        var (_, engine) = Create();

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 05"));

        Assert.Equal(Hex("46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 05 00 00 00 01"), reply.Response);
    }

    [Fact]
    public void V_F_3_ReadWords_AnswersTheVectorWithTheSourceAndDestinationSwapped()
    {
        var (memory, engine) = Create();
        Preset(memory, "D", 100, [0x1234, 0x5678]);

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 01 01 01 82 00 64 00 00 02"));

        Assert.Equal(Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 01 01 01 00 00 12 34 56 78"), reply.Response);
    }

    [Fact]
    public void V_F_5_WriteWord_StoresTheValueAndAnswersTheVector()
    {
        var (memory, engine) = Create();

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 1C 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 02 01 02 82 00 C8 00 00 01 00 FF"));

        Assert.Equal(Hex("46 49 4E 53 00 00 00 16 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 02 01 02 00 00"), reply.Response);
        Assert.Equal(new ushort[] { 0x00FF }, memory.FindWords("D")!.Read(200, 1));
    }

    [Fact]
    public void V_F_6_ReadBits_ReturnsOneByteForEachBit()
    {
        var (memory, engine) = Create();
        Preset(memory, "CIO", 0, [0x0005]);

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 03 01 01 30 00 00 00 00 03"));

        Assert.Equal(Hex("46 49 4E 53 00 00 00 19 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 03 01 01 00 00 01 00 01"), reply.Response);
    }

    [Fact]
    public void V_F_7_WriteBit_SetsTheBitOfTheWord()
    {
        var (memory, engine) = Create();

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 1B 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 04 01 02 31 00 0A 03 00 01 01"));

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
        Assert.Equal(new ushort[] { 0x0008 }, memory.FindWords("W")!.Read(10, 1));
    }

    [Fact]
    public void V_F_8_AddressOutOfRange_IsEndCode1103()
    {
        var (_, engine) = Create();

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 05 01 01 82 FF FF 00 00 01"));

        Assert.Equal(Hex("46 49 4E 53 00 00 00 16 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 05 01 01 11 03"), reply.Response);
    }

    [Fact]
    public void UnknownArea_IsEndCode1101()
    {
        var (_, engine) = Create();

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 06 01 01 99 00 00 00 00 01"));

        Assert.Equal(new byte[] { 0x11, 0x01 }, reply.Response![^2..]);
    }

    [Fact]
    public void FrameForAnotherNode_IsIgnored()
    {
        var (_, engine) = Create();

        SimulatorReply reply = Send(engine, Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 07 00 00 0B 00 07 01 01 82 00 64 00 00 02"));

        Assert.Equal(SimulatorReplyKind.NoReply, reply.Kind);
    }

    [Fact]
    public void FinsTcp_UsesTheLengthFieldAtOffsetFour_AndUdpHasNoCodec()
    {
        var (_, engine) = Create();
        byte[] request = Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 01 01 01 82 00 64 00 00 02");

        ReadOnlySequence<byte> buffer = new(request);

        Assert.True(engine.CreateRequestCodec(SimulatorTransport.Tcp)!.CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(request, frame.ToArray());
        Assert.Null(engine.CreateRequestCodec(SimulatorTransport.Udp));
    }

    [Fact]
    public void ForcedErrorCode_BecomesTheEndCodeOfTheResponse()
    {
        var (_, engine) = Create();
        var faults = new FaultPlan { ForcedErrorCode = 0x1103 };

        SimulatorReply reply = engine.Handle(
            Hex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 01 01 01 82 00 64 00 00 02"),
            new SimulatorConnection(1, null, new Dictionary<string, object?>()),
            faults);

        Assert.Equal(new byte[] { 0x11, 0x03 }, reply.Response![^2..]);
    }

    private static (SimulatorMemory Memory, FinsSlaveEngine Engine) Create()
    {
        var memory = new SimulatorMemory();
        return (memory, new FinsSlaveEngine(memory, Options));
    }

    private static SimulatorReply Send(FinsSlaveEngine engine, byte[] request) =>
        engine.Handle(request, new SimulatorConnection(1, null, new Dictionary<string, object?>()), FaultPlan.None);

    private static void Preset(SimulatorMemory memory, string area, int start, ushort[] values) =>
        memory.FindWords(area)!.Write(start, values);

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty));
}
