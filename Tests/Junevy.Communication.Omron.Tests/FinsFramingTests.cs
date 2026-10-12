using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Omron.Framing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS/TCP 分帧（计划 Task 13 第 1 条）：长度字段在偏移 4、宽 4、大端，值不含魔数与自身因此修正值为 0。
/// 分帧器由 <c>FrameCodecFactory</c> 生成，协议包不自己解码；最大帧长取 <see cref="FramingOptions.DefaultMaxFrameLength"/>。
/// </summary>
public sealed class FinsFramingTests
{
    // 与 FinsTcpFrame.ShellBytes 同一事实：魔数 4 + 长度 4。
    private const int MagicAndLengthBytes = 8;

    // 长度字段的值是"魔数与自身之后"的字节数，因此 4 字节字段能声明的最大帧长要减掉这 8 字节。
    private const int MaximumDeclaredLength = FramingOptions.DefaultMaxFrameLength - MagicAndLengthBytes;

    [Fact]
    public void Decoder_CutsEachFrameAtTheFinsLength()
    {
        // 修正值若不是 0，节点应答会被切成 12 字节或 28 字节；按小端读长度，00 00 00 0C 会变成 0x0C000000 而直接判为超长。
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Wire(V_F_1, V_F_2, V_F_4));
        IFrameDecoder decoder = FinsFraming.Create().CreateDecoder();

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> first));
        Assert.Equal(V_F_1, ToText(first.ToArray()));
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> second));
        Assert.Equal(V_F_2, ToText(second.ToArray()));
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> third));
        Assert.Equal(V_F_4, ToText(third.ToArray()));
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Decoder_WaitsForTheRestOfAPartialFrame()
    {
        byte[] frame = ToBytes(V_F_4);
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Truncated(V_F_4, frame.Length - 1));

        Assert.False(FinsFraming.Create().CreateDecoder().TryDecode(ref buffer, out _));
    }

    [Fact]
    public void Decoder_WaitsUntilTheLengthFieldItselfArrives()
    {
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Truncated(V_F_4, 7));

        Assert.False(FinsFraming.Create().CreateDecoder().TryDecode(ref buffer, out _));
    }

    [Fact]
    public void Decoder_RejectsAFrameLongerThanTheLimit()
    {
        // 长度字段是 4 字节，设备或链路噪声可以声明远超 65536 的帧长，必须由分帧器拒绝。
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Mutated(V_F_1, TcpLengthIndex, 0xFF));

        Assert.Throws<FrameDecodeException>(() => FinsFraming.Create().CreateDecoder().TryDecode(ref buffer, out _));
    }

    [Fact]
    public void Decoder_AcceptsTheLargestFrameTheLimitAllows()
    {
        const int frameBytes = FramingOptions.DefaultMaxFrameLength;
        var wire = new byte[frameBytes];
        wire[0] = 0x46;
        wire[1] = 0x49;
        wire[2] = 0x4E;
        wire[3] = 0x53;
        WriteBigEndian(wire, TcpLengthIndex, MaximumDeclaredLength);

        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(wire);

        Assert.True(FinsFraming.Create().CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(frameBytes, frame.Length);
    }

    [Fact]
    public void Encoder_WritesTheFrameUnchanged()
    {
        // FINS 自己写出完整的外壳（含长度字段），编码器不需要追加任何内容。
        using var output = new ByteBufferWriter();

        FinsFraming.Create().CreateEncoder().Encode(ToBytes(V_F_3), output);

        Assert.Equal(V_F_3, ToText(output.WrittenSpan));
    }

    [Fact]
    public void EachConnection_GetItsOwnDecoder()
    {
        IFrameCodecFactory codec = FinsFraming.Create();

        Assert.NotSame(codec.CreateDecoder(), codec.CreateDecoder());
    }

    private static void WriteBigEndian(byte[] frame, int index, int value)
    {
        frame[index] = (byte)(value >> 24);
        frame[index + 1] = (byte)(value >> 16);
        frame[index + 2] = (byte)(value >> 8);
        frame[index + 3] = (byte)value;
    }

    // 多条向量接到同一段字节里，模拟一次连接上连续到达的帧。
    private static byte[] Wire(params string[] vectors)
    {
        using var buffer = new ByteBufferWriter();
        foreach (string vector in vectors)
            buffer.Write(ToBytes(vector));

        return buffer.ToArray();
    }
}
