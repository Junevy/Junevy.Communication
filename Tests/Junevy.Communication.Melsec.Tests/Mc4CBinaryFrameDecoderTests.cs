using System.Buffers;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Melsec.Framing;
using Junevy.Communication.Testing;
using static Junevy.Communication.Melsec.Tests.McTestSupport;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 格式 5 分帧器（计划 Task 10）：半帧分次到达、帧前垃圾、DLE 对跨越两次到达的边界、和校验开关、帧长上限，
/// 以及工厂的编码器原样输出。测试帧是第 18 节 V8 的应答（附加码与和校验均按手册规则）。
/// </summary>
public sealed class Mc4CBinaryFrameDecoderTests
{
    private static readonly byte[] Response = Hex("10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03 33 37");

    // DLE ETX 之后的位置：帧体（不含和校验）为 26 字节。
    private const int BodyLength = 26;

    [Fact(Timeout = 20000)]
    public void HalfFrame_IsNotDelivered_UntilTheRestArrives()
    {
        var decoder = new Mc4CBinaryFrameDecoder(true, 256);
        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Part(Response, 0, 6));
        Assert.False(decoder.TryDecode(ref buffer, out _));
        Assert.Equal(6, buffer.Length);

        buffer = SequenceFactory.FromSegments(Part(Response, 0, 6), Part(Response, 6, Response.Length - 6));
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(Response, frame.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact(Timeout = 20000)]
    public void DlePairSplitAcrossTwoArrivals_IsOneEscapedByte_NotADleEtx()
    {
        // 第一次到达以 10H 结束，第二次以 10H 开始：两个 10H 是同一个附加码，不能当作 DLE ETX。
        var decoder = new Mc4CBinaryFrameDecoder(true, 256);
        ReadOnlySequence<byte> first = SequenceFactory.FromSegments(Part(Response, 0, 18));
        Assert.False(decoder.TryDecode(ref first, out _));

        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Part(Response, 0, 18), Part(Response, 18, Response.Length - 18));
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(Response, frame.ToArray());
    }

    [Fact(Timeout = 20000)]
    public void GarbageBeforeDleStx_ThrowsFrameDecodeException()
    {
        var decoder = new Mc4CBinaryFrameDecoder(true, 256);
        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Hex("AA 10 02 10 03"));

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    [Fact(Timeout = 20000)]
    public void InvalidDleSequence_ThrowsFrameDecodeException()
    {
        var decoder = new Mc4CBinaryFrameDecoder(false, 256);
        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Hex("10 02 10 05 00"));

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    [Fact(Timeout = 20000)]
    public void ChecksumOff_FrameEndsAtDleEtx_AndLeavesTheRestInTheBuffer()
    {
        var decoder = new Mc4CBinaryFrameDecoder(false, 256);
        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Response);

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(Part(Response, 0, BodyLength), frame.ToArray());
        Assert.Equal(2, buffer.Length);
    }

    [Fact(Timeout = 20000)]
    public void ChecksumOn_FrameIncludesTheTwoChecksumBytes()
    {
        var decoder = new Mc4CBinaryFrameDecoder(true, 256);
        ReadOnlySequence<byte> incomplete = SequenceFactory.FromSegments(Part(Response, 0, Response.Length - 1));
        Assert.False(decoder.TryDecode(ref incomplete, out _));

        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Response);
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(Response, frame.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact(Timeout = 20000)]
    public void TwoFramesInOneBuffer_AreDeliveredInOrder()
    {
        var decoder = new Mc4CBinaryFrameDecoder(false, 256);
        byte[] second = Hex("10 02 0C 00 F8 00 00 FF FF 03 00 00 FF FF 51 C0 10 03");
        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Join(Part(Response, 0, BodyLength), second));

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> first));
        Assert.Equal(Part(Response, 0, BodyLength), first.ToArray());
        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> next));
        Assert.Equal(second, next.ToArray());
    }

    [Fact(Timeout = 20000)]
    public void FrameLongerThanMaxFrameLength_ThrowsFrameDecodeException()
    {
        var decoder = new Mc4CBinaryFrameDecoder(false, 20);
        ReadOnlySequence<byte> buffer = SequenceFactory.FromSegments(Response);

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    [Fact(Timeout = 20000)]
    public void CodecFactory_EncoderPassesTheFrameThrough()
    {
        var factory = new Mc4CBinaryFrameCodecFactory(true, 256);
        var output = new ByteBufferWriter();

        factory.CreateEncoder().Encode(Response, output);

        Assert.Equal(Response, output.ToArray());
    }

    private static byte[] Part(byte[] bytes, int start, int length) => bytes.Skip(start).Take(length).ToArray();
}
