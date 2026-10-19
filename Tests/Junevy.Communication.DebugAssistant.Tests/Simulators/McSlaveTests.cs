using System.Buffers;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Melsec;
using Junevy.Communication.DebugAssistant.Simulators.Memory;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>
/// MELSEC 从站的黄金向量（计划 Task 12 第 2 条，P2 计划第 18 节 V1–V10）：3E、4E 的二进制与 ASCII，4C 格式 4 与格式 5，
/// 环回（0619），结束码错误，以及 4C 格式 5 的 DLE 转义与和校验。
/// </summary>
public sealed class McSlaveTests
{
    private const string DefaultAsciiAccess = "00FF03FF00";

    [Fact]
    public void V1_Binary3E_ReadsWords_AnswersTheVector()
    {
        var (memory, engine) = Create(McFrameKind.Frame3E, McEncodingKind.Binary);
        PresetWords(memory, "TN", 100, [0x1234, 0x0002, 0x1DEF]);

        SimulatorReply reply = Send(engine, Hex("50 00 00 FF FF 03 00 0C 00 10 00 01 04 00 00 64 00 00 C2 03 00"));

        Assert.Equal(Hex("D0 00 00 FF FF 03 00 08 00 00 00 34 12 02 00 EF 1D"), reply.Response);
    }

    [Fact]
    public void V2_Ascii3E_ReadsWords_AnswersTheVector()
    {
        var (memory, engine) = Create(McFrameKind.Frame3E, McEncodingKind.Ascii);
        PresetWords(memory, "TN", 100, [0x1234, 0x0002, 0x1DEF]);

        SimulatorReply reply = Send(engine, Text("5000" + DefaultAsciiAccess + "0018" + "0010" + "04010000TN0001000003"));

        Assert.Equal(Text("D000" + DefaultAsciiAccess + "0010" + "0000" + "123400021DEF"), reply.Response);
    }

    [Fact]
    public void V3_Binary4E_ReadsBits_PackedTwoPointsPerByte()
    {
        var (memory, engine) = Create(McFrameKind.Frame4E, McEncodingKind.Binary);
        PresetBits(memory, "M", 100, [false, false, false, true, false, false, true, true]);

        SimulatorReply reply = Send(engine, Hex("54 00 34 12 00 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 90 08 00"));

        Assert.Equal(Hex("D4 00 34 12 00 00 00 FF FF 03 00 06 00 00 00 00 01 00 11"), reply.Response);
    }

    [Fact]
    public void V4_Ascii4E_WritesWords_AnswersTheVector()
    {
        var (memory, engine) = Create(McFrameKind.Frame4E, McEncodingKind.Ascii);

        SimulatorReply reply = Send(engine, Text("5400" + "1234" + "0000" + DefaultAsciiAccess + "0024" + "0010" + "14010000D*0001000003199512021130"));

        Assert.Equal(Text("D400" + "1234" + "0000" + DefaultAsciiAccess + "0004" + "0000"), reply.Response);
        Assert.Equal(new ushort[] { 0x1995, 0x1202, 0x1130 }, memory.FindWords("D")!.Read(100, 3));
    }

    [Fact]
    public void V5_ErrorEndCode_CarriesTheErrorInformation()
    {
        var (_, engine) = Create(McFrameKind.Frame3E, McEncodingKind.Binary);
        var faults = new FaultPlan { ForcedErrorCode = 0xC051 };

        SimulatorReply reply = engine.Handle(Hex("50 00 00 FF FF 03 00 0C 00 10 00 01 04 00 00 64 00 00 C2 03 00"), Connection(), faults);

        Assert.Equal(Hex("D0 00 00 FF FF 03 00 0B 00 51 C0 00 FF FF 03 00 01 04 00 00"), reply.Response);
    }

    [Fact]
    public void V6_Loopback_EchoesTheData()
    {
        var (_, engine) = Create(McFrameKind.Frame3E, McEncodingKind.Binary);

        SimulatorReply reply = Send(engine, Hex("50 00 00 FF FF 03 00 0D 00 10 00 19 06 00 00 05 00 41 42 43 44 45"));

        Assert.Equal(Hex("D0 00 00 FF FF 03 00 09 00 00 00 05 00 41 42 43 44 45"), reply.Response);
    }

    [Fact]
    public void V9_4CFormat4_ReadsWords_WithTheChecksumIncludingEtx()
    {
        var (memory, engine) = Create(McFrameKind.Frame4C, McEncodingKind.Ascii);
        PresetWords(memory, "TN", 100, [0x1234, 0x0002, 0x1DEF]);
        string payload = "F80000FF03FF000004010000TN0001000003" + "84";

        SimulatorReply reply = Send(engine, Text(payload));

        Assert.Equal(Hex("02") .Concat(Text("F80000FF03FF0000123400021DEF")).Concat(Hex("03 30 38 0D 0A")).ToArray(), reply.Response);
    }

    [Fact]
    public void V10_4CFormat4_Error_IsNakWithTheEndCode()
    {
        var (_, engine) = Create(McFrameKind.Frame4C, McEncodingKind.Ascii);
        var faults = new FaultPlan { ForcedErrorCode = 0xC051 };

        SimulatorReply reply = engine.Handle(Text("F80000FF03FF000004010000TN0001000003" + "84"), Connection(), faults);

        Assert.Equal(Hex("15").Concat(Text("F80000FF03FF0000C051")).Concat(Hex("0D 0A")).ToArray(), reply.Response);
    }

    [Fact]
    public void V7_4CFormat5_RequestIsDecodedAndAnswered_WithDleFraming()
    {
        var (memory, engine) = Create(McFrameKind.Frame4C, McEncodingKind.Binary);
        PresetBits(memory, "X", 0x40, [true, false, true, true, false]);
        byte[] request = Hex("10 02 12 00 F8 05 07 03 04 00 01 00 01 04 01 00 40 00 00 9C 05 00 10 03 30 35");

        ReadOnlySequence<byte> buffer = new(request);
        Assert.True(engine.CreateRequestCodec(SimulatorTransport.Serial)!.CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(request, frame.ToArray());

        SimulatorReply reply = Send(engine, request);

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
        Assert.Equal(new byte[] { 0x10, 0x02 }, reply.Response![..2]);
    }

    [Fact]
    public void V8_4CFormat5_DleInTheDeviceNumber_IsUnescaped_AndTheWordsAreEscapedInTheAnswer()
    {
        var (memory, engine) = Create(McFrameKind.Frame4C, McEncodingKind.Binary);

        // M16 起的 32 个点：字 0010H 为 M20，字 1010H 为 M36 与 M44（字单位读取，每字 16 点）。
        bool[] bits = new bool[32];
        bits[4] = true;
        bits[20] = true;
        bits[28] = true;
        PresetBits(memory, "M", 16, bits);
        byte[] request = Hex("10 02 12 00 F8 00 00 FF FF 03 00 00 01 04 00 00 10 10 00 00 90 02 00 10 03 42 32");

        SimulatorReply reply = Send(engine, request);

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
        Assert.Equal(new byte[] { 0x10, 0x02 }, reply.Response![..2]);

        // 数据 10 00 10 10（两个字，小端）在应答中按 DLE 规则转义为 10 10 00 10 10 10 10，位于校验之前。
        Assert.Equal(Hex("10 10 00 10 10 10 10"), reply.Response[^11..^4]);
    }

    [Fact]
    public void V8_4CFormat5_Answer_CarriesTheChecksumOfTheUnescapedBody()
    {
        var (memory, engine) = Create(McFrameKind.Frame4C, McEncodingKind.Binary);
        bool[] bits = new bool[32];
        bits[4] = true;
        bits[20] = true;
        bits[28] = true;
        PresetBits(memory, "M", 16, bits);
        byte[] request = Hex("10 02 12 00 F8 00 00 FF FF 03 00 00 01 04 00 00 10 10 00 00 90 02 00 10 03 42 32");

        byte[] response = Send(engine, request).Response!;

        // 和校验是 DLE STX 与 DLE ETX 之间、去掉转义后所有字节之和的低字节，写成两个 ASCII 字符。
        byte[] unescaped = Unescape(response[2..^4]);
        byte expected = (byte)unescaped.Sum(value => value);
        Assert.Equal(expected.ToString("X2"), Encoding.ASCII.GetString(response[^2..]));
    }

    [Fact]
    public void V8_4CFormat5_Answer_MatchesTheDecodeVectorByteForByte()
    {
        var (memory, engine) = Create(McFrameKind.Frame4C, McEncodingKind.Binary);
        bool[] bits = new bool[32];
        bits[4] = true;
        bits[20] = true;
        bits[28] = true;
        PresetBits(memory, "M", 16, bits);
        byte[] request = Hex("10 02 12 00 F8 00 00 FF FF 03 00 00 01 04 00 00 10 10 00 00 90 02 00 10 03 42 32");

        SimulatorReply reply = Send(engine, request);

        // 应答为 F8、访问路径、响应 ID FF FF、完成码 00 00 与数据；和校验 37 是去转义后的字节之和（含长度）的低字节。
        Assert.Equal(Hex("10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03 33 37"), reply.Response);
    }

    [Fact]
    public void Format5WithoutSumCheck_RequestEndsAtDleEtx_AndTheAnswerHasNoChecksum()
    {
        var memory = new SimulatorMemory();
        var engine = new McSlaveEngine(memory, new McSlaveOptions(McFrameKind.Frame4C, McEncodingKind.Binary, SumCheck: false));
        bool[] bits = new bool[32];
        bits[4] = true;
        bits[20] = true;
        bits[28] = true;
        PresetBits(memory, "M", 16, bits);
        byte[] request = Hex("10 02 12 00 F8 00 00 FF FF 03 00 00 01 04 00 00 10 10 00 00 90 02 00 10 03");

        SimulatorReply reply = Send(engine, request);

        Assert.Equal(SimulatorReplyKind.Reply, reply.Kind);
        Assert.Equal(Hex("10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03"), reply.Response);
    }

    [Fact]
    public void Binary3E_UnknownCommand_IsEndCodeC059()
    {
        var (_, engine) = Create(McFrameKind.Frame3E, McEncodingKind.Binary);

        SimulatorReply reply = Send(engine, Hex("50 00 00 FF FF 03 00 0C 00 10 00 02 01 00 00 64 00 00 C2 03 00"));

        Assert.Equal(Hex("D0 00 00 FF FF 03 00 0B 00 59 C0 00 FF FF 03 00 02 01 00 00"), reply.Response);
    }

    [Fact]
    public void FrameDecoder_4CFormat4_StripsEnqAndCrlf()
    {
        var (_, engine) = Create(McFrameKind.Frame4C, McEncodingKind.Ascii);
        IFrameDecoder decoder = engine.CreateRequestCodec(SimulatorTransport.Serial)!.CreateDecoder();
        ReadOnlySequence<byte> buffer = new(Hex("05").Concat(Text("F80000FF03FF0000")).Concat(Hex("0D 0A")).ToArray());

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(Text("F80000FF03FF0000"), frame.ToArray());
    }

    [Fact]
    public void UdpDatagrams_HaveNoFramingCodec()
    {
        var (_, engine) = Create(McFrameKind.Frame3E, McEncodingKind.Binary);

        Assert.Null(engine.CreateRequestCodec(SimulatorTransport.Udp));
    }

    private static (SimulatorMemory Memory, McSlaveEngine Engine) Create(McFrameKind frame, McEncodingKind encoding)
    {
        var memory = new SimulatorMemory();
        return (memory, new McSlaveEngine(memory, new McSlaveOptions(frame, encoding)));
    }

    private static SimulatorConnection Connection() => new(1, null, new Dictionary<string, object?>());

    private static SimulatorReply Send(McSlaveEngine engine, byte[] request) =>
        engine.Handle(request, Connection(), FaultPlan.None);

    private static void PresetWords(SimulatorMemory memory, string device, int start, ushort[] values) =>
        memory.FindWords(device)!.Write(start, values);

    private static void PresetBits(SimulatorMemory memory, string device, int start, bool[] values) =>
        memory.FindBits(device)!.Write(start, values);

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty));

    /// <summary>去掉 DLE 转义（DLE DLE 还原为 DLE）。</summary>
    private static byte[] Unescape(byte[] escaped)
    {
        var result = new List<byte>();
        for (int index = 0; index < escaped.Length; index++)
        {
            result.Add(escaped[index]);
            if (escaped[index] == 0x10 && index + 1 < escaped.Length && escaped[index + 1] == 0x10)
            {
                index++;
            }
        }

        return [.. result];
    }

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);
}
