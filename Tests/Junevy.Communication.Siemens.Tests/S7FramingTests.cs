using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Siemens.Framing;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// TPKT 分帧（计划第 12 节第 1 条、设计 5.2 的 S7 行）：长度字段在偏移 2、宽 2、大端、修正值 −4，
/// 最大帧长取 <see cref="FramingOptions.DefaultMaxFrameLength"/>。分帧器由 <c>FrameCodecFactory</c> 生成，协议包不自己解码。
/// </summary>
public sealed class S7FramingTests
{
    // TPKT 长度字段最大能表示 65535，因此这是 S7 单帧的上限，必须落在默认的最大帧长之内。
    private const int TpktMaximumFrameLength = 0xFFFF;

    [Fact]
    public void Decoder_CutsEachFrameAtTheTpktLength()
    {
        // 若修正值不是 −4，第一帧会多切 4 字节；若按小端读长度，00 16 会变成 0x1600 而直接判为超长。
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Concat(V_S7_1, V_S7_2, V_S7_6));
        IFrameDecoder decoder = S7Framing.Create().CreateDecoder();

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> first));
        Assert.Equal(V_S7_1, ToText(first.ToArray()));
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> second));
        Assert.Equal(V_S7_2, ToText(second.ToArray()));
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> third));
        Assert.Equal(V_S7_6, ToText(third.ToArray()));
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Decoder_WaitsForTheRestOfAPartialFrame()
    {
        byte[] frame = ToBytes(V_S7_6);
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Truncated(V_S7_6, frame.Length - 1));

        Assert.False(S7Framing.Create().CreateDecoder().TryDecode(ref buffer, out _));
    }

    [Fact]
    public void Decoder_WaitsUntilTheLengthFieldItselfArrives()
    {
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Truncated(V_S7_6, 3));

        Assert.False(S7Framing.Create().CreateDecoder().TryDecode(ref buffer, out _));
    }

    [Fact]
    public void Decoder_RejectsALengthShorterThanTpktItself()
    {
        // 长度字段含 TPKT 自身，说 2 字节的帧不可能是合法 S7 应答。
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Mutated(V_S7_2, TpktLengthLow, 0x02));

        Assert.Throws<FrameDecodeException>(() => S7Framing.Create().CreateDecoder().TryDecode(ref buffer, out _));
    }

    [Fact]
    public void Decoder_AcceptsTheLargestTpktFrame()
    {
        var wire = new byte[TpktMaximumFrameLength];
        wire[0] = 0x03;
        wire[2] = 0xFF;
        wire[3] = 0xFF;
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(wire);

        Assert.True(S7Framing.Create().CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(TpktMaximumFrameLength, frame.Length);
    }

    [Fact]
    public void Encoder_WritesTheFrameUnchanged()
    {
        // S7 自己写出完整的 TPKT 头，编码器不需要追加任何内容。
        using var output = new ByteBufferWriter();

        S7Framing.Create().CreateEncoder().Encode(ToBytes(V_S7_3), output);

        Assert.Equal(V_S7_3, ToText(output.WrittenSpan));
    }

    [Fact]
    public void EachConnection_GetItsOwnDecoder()
    {
        IFrameCodecFactory codec = S7Framing.Create();

        Assert.NotSame(codec.CreateDecoder(), codec.CreateDecoder());
    }
}
