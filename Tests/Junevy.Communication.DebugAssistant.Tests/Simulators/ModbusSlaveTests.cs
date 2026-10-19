using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;
using Junevy.Communication.DebugAssistant.Simulators.Modbus;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>
/// Modbus 从站的黄金向量（计划 Task 12 第 2 条，P7 计划第 21.1–21.4 节）：PDU、RTU（CRC）、MBAP 逐字节，
/// 以及 RTU 请求分帧的长度与重新同步。CRC 在测试中用独立的实现计算，与引擎的实现互相核对。
/// </summary>
public sealed class ModbusSlaveTests
{
    private static readonly byte[] Units = [1, 2, 0xFF];

    /// <summary>V-P-1 的线圈 19–37：1 0 1 1 0 0 1 1，1 1 0 1 0 1 1 0，1 0 1。</summary>
    private static readonly int[] CoilPattern = [1, 0, 1, 1, 0, 0, 1, 1, 1, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1];

    [Theory]
    [InlineData("05 00 AC FF 00", "05 00 AC FF 00")]
    [InlineData("06 00 01 00 03", "06 00 01 00 03")]
    [InlineData("16 00 04 00 F2 00 25", "16 00 04 00 F2 00 25")]
    [InlineData("16 00 64 FF DF 00 20", "16 00 64 FF DF 00 20")]
    [InlineData("16 00 64 FF DF 00 00", "16 00 64 FF DF 00 00")]
    [InlineData("05 00 00 00 00", "05 00 00 00 00")]
    [InlineData("06 00 64 12 34", "06 00 64 12 34")]
    [InlineData("0F 00 13 00 0A 02 CD 01", "0F 00 13 00 0A")]
    [InlineData("10 00 01 00 02 04 00 0A 01 02", "10 00 01 00 02")]
    [InlineData("0F 00 00 00 03 01 05", "0F 00 00 00 03")]
    public void Pdu_WritesAnswerAsTheVectorsSay_OverMbap(string request, string response)
    {
        var (_, engine) = Create(ModbusFrameMode.Mbap);

        SimulatorReply reply = Send(engine, Mbap(1, 1, Hex(request)));

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
        Assert.Equal(Mbap(1, 1, Hex(response)), reply.Response);
    }

    [Fact]
    public void Pdu_ReadCoils_ReturnsTheBitsLeastSignificantFirst()
    {
        var (memory, engine) = Create(ModbusFrameMode.Mbap);
        PresetCoils(memory, 19, CoilPattern);

        SimulatorReply reply = Send(engine, Mbap(1, 1, Hex("01 00 13 00 13")));

        Assert.Equal(Mbap(1, 1, Hex("01 03 CD 6B 05")), reply.Response);
    }

    [Fact]
    public void Pdu_ReadDiscreteInputs_ReturnsTheVectorBytes()
    {
        var (memory, engine) = Create(ModbusFrameMode.Mbap);
        PresetInputs(memory, 196, Hex("AC DB 35"), 22);

        SimulatorReply reply = Send(engine, Mbap(1, 1, Hex("02 00 C4 00 16")));

        Assert.Equal(Mbap(1, 1, Hex("02 03 AC DB 35")), reply.Response);
    }

    [Fact]
    public void Pdu_ReadHoldingRegisters_ReturnsTheValuesBigEndian()
    {
        var (memory, engine) = Create(ModbusFrameMode.Mbap);
        PresetWords(memory, ModbusSlaveEngine.HoldingArea, 107, [555, 0, 100]);

        SimulatorReply reply = Send(engine, Mbap(1, 1, Hex("03 00 6B 00 03")));

        Assert.Equal(Mbap(1, 1, Hex("03 06 02 2B 00 00 00 64")), reply.Response);
    }

    [Fact]
    public void Pdu_ReadInputRegisters_ReturnsTheVector()
    {
        var (memory, engine) = Create(ModbusFrameMode.Mbap);
        PresetWords(memory, ModbusSlaveEngine.RegisterArea, 8, [0x000A]);

        SimulatorReply reply = Send(engine, Mbap(1, 1, Hex("04 00 08 00 01")));

        Assert.Equal(Mbap(1, 1, Hex("04 02 00 0A")), reply.Response);
    }

    [Fact]
    public void Pdu_WriteSingleCoil_SetsTheCoil()
    {
        var (memory, engine) = Create(ModbusFrameMode.Mbap);

        Send(engine, Mbap(1, 1, Hex("05 00 AC FF 00")));

        Assert.True(memory.FindBits(ModbusSlaveEngine.CoilArea)!.Read(172, 1)[0]);
    }

    [Fact]
    public void Pdu_ReadWriteRegisters_WritesFirst_ThenReadsTheVectorValues()
    {
        var (memory, engine) = Create(ModbusFrameMode.Mbap);
        PresetWords(memory, ModbusSlaveEngine.HoldingArea, 3, [0x00FE, 0x0ACD, 0x0001, 0x0003, 0x000D, 0x00FF]);

        SimulatorReply reply = Send(engine, Mbap(1, 1, Hex("17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF")));

        Assert.Equal(Mbap(1, 1, Hex("17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF")), reply.Response);
        Assert.Equal(new ushort[] { 0x00FF, 0x00FF, 0x00FF }, memory.FindWords(ModbusSlaveEngine.HoldingArea)!.Read(14, 3));
    }

    [Fact]
    public void Pdu_ReadOutsideTheArea_IsException02()
    {
        var (_, engine) = Create(ModbusFrameMode.Mbap);

        SimulatorReply reply = Send(engine, Mbap(3, 1, Hex("03 FF FF 00 02")));

        Assert.Equal(Mbap(3, 1, Hex("83 02")), reply.Response);
    }

    [Fact]
    public void Pdu_UnknownFunction_IsException01()
    {
        var (_, engine) = Create(ModbusFrameMode.Mbap);

        SimulatorReply reply = Send(engine, Mbap(4, 1, Hex("2B 0E 01 00")));

        Assert.Equal(Mbap(4, 1, Hex("AB 01")), reply.Response);
    }

    [Fact]
    public void Mbap_TransactionAndUnitAreEchoed_AndTheUnitFFIsAccepted()
    {
        var (memory, engine) = Create(ModbusFrameMode.Mbap);
        PresetCoils(memory, 19, CoilPattern);

        SimulatorReply reply = Send(engine, Hex("00 04 00 00 00 06 FF 01 00 13 00 13"));

        Assert.Equal(Hex("00 04 00 00 00 06 FF 01 03 CD 6B 05"), reply.Response);
    }

    [Fact]
    public void Mbap_ProtocolIdentifierOtherThanZero_IsIgnored()
    {
        var (_, engine) = Create(ModbusFrameMode.Mbap);

        SimulatorReply reply = Send(engine, Hex("00 05 00 01 00 05 01 03 02 00 01"));

        Assert.Equal(SimulatorReplyKind.NoReply, reply.Kind);
    }

    [Fact]
    public void UnknownUnit_IsIgnored()
    {
        var (_, engine) = Create(ModbusFrameMode.Mbap);

        SimulatorReply reply = Send(engine, Mbap(1, 7, Hex("03 00 00 00 01")));

        Assert.Equal(SimulatorReplyKind.NoReply, reply.Kind);
    }

    [Theory]
    [InlineData("01 03 00 00 00 0A C5 CD")]
    [InlineData("01 03 00 00 00 01 84 0A")]
    public void Rtu_KnownCrcSamples_AreAccepted(string request)
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);

        SimulatorReply reply = Send(engine, Hex(request));

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
    }

    [Theory]
    [InlineData("01 06 00 01 00 03 98 0B")]
    [InlineData("01 05 00 00 FF 00 8C 3A")]
    [InlineData("01 05 00 AC FF 00 4C 1B")]
    [InlineData("01 16 00 04 00 F2 00 25 67 EE")]
    [InlineData("01 16 00 64 FF DF 00 20 87 F8")]
    [InlineData("01 16 00 64 FF DF 00 00 86 20")]
    [InlineData("01 06 00 64 12 34 C5 62")]
    [InlineData("01 05 00 00 00 00 CD CA")]
    public void Rtu_EchoVectors_AnswerWithTheSameFrame(string request)
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);

        SimulatorReply reply = Send(engine, Hex(request));

        Assert.Equal(Hex(request), reply.Response);
    }

    [Theory]
    [InlineData("01 0F 00 13 00 0A 02 CD 01 72 CB", "01 0F 00 13 00 0A 24 09")]
    [InlineData("01 10 00 01 00 02 04 00 0A 01 02 92 30", "01 10 00 01 00 02 10 08")]
    public void Rtu_WriteMultipleVectors_AnswerWithTheVectorFrame(string request, string response)
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);

        SimulatorReply reply = Send(engine, Hex(request));

        Assert.Equal(Hex(response), reply.Response);
    }

    [Fact]
    public void Rtu_ReadVectors_AnswerWithTheVectorFrames()
    {
        var (memory, engine) = Create(ModbusFrameMode.Rtu);
        PresetWords(memory, ModbusSlaveEngine.HoldingArea, 107, [555, 0, 100]);
        PresetCoils(memory, 19, CoilPattern);
        PresetInputs(memory, 196, Hex("AC DB 35"), 22);
        PresetWords(memory, ModbusSlaveEngine.RegisterArea, 8, [0x000A]);

        Assert.Equal(Hex("01 03 06 02 2B 00 00 00 64 05 7A"), Send(engine, Hex("01 03 00 6B 00 03 74 17")).Response);
        Assert.Equal(Hex("01 01 03 CD 6B 05 42 82"), Send(engine, Hex("01 01 00 13 00 13 8C 02")).Response);
        Assert.Equal(Hex("01 02 03 AC DB 35 22 88"), Send(engine, Hex("01 02 00 C4 00 16 B8 39")).Response);
        Assert.Equal(Hex("01 04 02 00 0A 39 37"), Send(engine, Hex("01 04 00 08 00 01 B0 08")).Response);
    }

    [Fact]
    public void Rtu_ReadWriteMultiple_AnswerWithTheVectorFrame()
    {
        var (memory, engine) = Create(ModbusFrameMode.Rtu);
        PresetWords(memory, ModbusSlaveEngine.HoldingArea, 3, [0x00FE, 0x0ACD, 0x0001, 0x0003, 0x000D, 0x00FF]);

        SimulatorReply reply = Send(engine, Hex("01 17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF 46 91"));

        Assert.Equal(Hex("01 17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF 1D 79"), reply.Response);
    }

    [Fact]
    public void Rtu_ExceptionVectors_AnswerWithTheVectorFrames()
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);

        // V-R-15：读的地址越界，异常码 02。请求的 CRC 由测试中的独立实现计算。
        Assert.Equal(Hex("01 83 02 C0 F1"), Send(engine, WithCrc("01 03 FF FF 00 02")).Response);

        // V-R-20：写多个寄存器的数量为 0，异常码 03。
        Assert.Equal(Hex("01 90 03 0C 01"), Send(engine, WithCrc("01 10 00 00 00 00")).Response);
    }

    [Fact]
    public void Rtu_StationTwo_ReadsItsOwnRegisters()
    {
        var (memory, engine) = Create(ModbusFrameMode.Rtu);
        PresetWords(memory, ModbusSlaveEngine.HoldingArea, 100, [0x1234, 0x5678]);

        Assert.Equal(Hex("02 03 04 12 34 56 78 B2 07"), Send(engine, Hex("02 03 00 64 00 02 85 E7")).Response);
    }

    [Fact]
    public void Rtu_CorruptChecksum_FlipsTheCrcOfTheAnswer()
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);
        var faults = new FaultPlan { CorruptChecksum = true };

        SimulatorReply reply = engine.Handle(Hex("01 06 00 01 00 03 98 0B"), Connection(), faults);

        Assert.Equal(Hex("01 06 00 01 00 03 98 0A"), reply.Response);
    }

    [Fact]
    public void Rtu_WrongCrc_IsIgnored()
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);

        SimulatorReply reply = Send(engine, Hex("01 03 00 00 00 0A C5 CC"));

        Assert.Equal(SimulatorReplyKind.NoReply, reply.Kind);
    }

    [Fact]
    public void ForcedErrorCode_BecomesAnExceptionResponse()
    {
        var (_, engine) = Create(ModbusFrameMode.Mbap);
        var faults = new FaultPlan { ForcedErrorCode = 0x0004 };

        SimulatorReply reply = engine.Handle(Mbap(5, 1, Hex("03 00 00 00 01")), Connection(), faults);

        Assert.Equal(Mbap(5, 1, Hex("83 04")), reply.Response);
    }

    [Fact]
    public void Decoder_TakesEachRequestLengthFromItsFunctionCode()
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);
        IFrameCodecFactory factory = engine.CreateRequestCodec(SimulatorTransport.Serial)!;
        byte[][] requests =
        [
            WithCrc("01 03 00 00 00 0A"),
            WithCrc("01 06 00 01 00 03"),
            WithCrc("01 0F 00 13 00 0A 02 CD 01"),
            WithCrc("01 10 00 01 00 02 04 00 0A 01 02"),
            WithCrc("01 16 00 04 00 F2 00 25"),
            WithCrc("01 17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF"),
        ];
        foreach (byte[] request in requests)
        {
            ReadOnlySequence<byte> buffer = new(request);
            Assert.True(factory.CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
            Assert.Equal(request, frame.ToArray());
            Assert.Equal(0, buffer.Length);
        }
    }

    [Fact]
    public void Decoder_ResynchronisesAfterGarbage_AndWaitsForAPartialFrame()
    {
        var (_, engine) = Create(ModbusFrameMode.Rtu);
        IFrameDecoder decoder = engine.CreateRequestCodec(SimulatorTransport.Serial)!.CreateDecoder();
        byte[] request = WithCrc("01 03 00 00 00 01");
        byte[] partial = [0xEE, 0x77, .. request[..5]];

        ReadOnlySequence<byte> incomplete = new(partial);
        Assert.False(decoder.TryDecode(ref incomplete, out _));

        ReadOnlySequence<byte> complete = new([0xEE, 0x77, .. request]);
        Assert.True(decoder.TryDecode(ref complete, out ReadOnlySequence<byte> frame));
        Assert.Equal(request, frame.ToArray());
    }

    private static (SimulatorMemory Memory, ModbusSlaveEngine Engine) Create(ModbusFrameMode frame)
    {
        var memory = new SimulatorMemory();
        return (memory, new ModbusSlaveEngine(memory, new ModbusSlaveOptions(Units, frame)));
    }

    private static SimulatorConnection Connection() => new(1, null, new Dictionary<string, object?>());

    private static SimulatorReply Send(ModbusSlaveEngine engine, byte[] request) =>
        engine.Handle(request, Connection(), FaultPlan.None);

    /// <summary>线圈的预置：1 为 ON，0 为 OFF，从 <paramref name="start"/> 开始。</summary>
    private static void PresetCoils(SimulatorMemory memory, int start, int[] bits) =>
        memory.FindBits(ModbusSlaveEngine.CoilArea)!.Write(start, Array.ConvertAll(bits, bit => bit != 0));

    /// <summary>离散输入的预置：按每字节最低位在前的打包字节展开。</summary>
    private static void PresetInputs(SimulatorMemory memory, int start, byte[] packed, int count) =>
        memory.FindBits(ModbusSlaveEngine.InputArea)!.Write(start, Enumerable.Range(0, count).Select(index => (packed[index / 8] & (1 << (index % 8))) != 0).ToArray());

    private static void PresetWords(SimulatorMemory memory, string area, int start, ushort[] values) =>
        memory.FindWords(area)!.Write(start, values);

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty));

    private static byte[] Mbap(ushort transaction, byte unit, byte[] pdu)
    {
        int length = pdu.Length + 1;
        return [(byte)(transaction >> 8), (byte)transaction, 0, 0, (byte)(length >> 8), (byte)length, unit, .. pdu];
    }

    /// <summary>给 RTU 请求加上测试中独立计算的 CRC-16/MODBUS（低字节在前）。</summary>
    private static byte[] WithCrc(string text)
    {
        byte[] body = Hex(text);
        ushort crc = 0xFFFF;
        foreach (byte value in body)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return [.. body, (byte)crc, (byte)(crc >> 8)];
    }
}
