using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Modbus.Framing;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Framing;

/// <summary>
/// 关联键（P7-D9，第 21.3 节 V-M-6）与两种字节流分帧的装配：MBAP 按长度字段分帧，RTU 按功能码分帧。
/// </summary>
public sealed class ModbusFramingTests
{
    private const string MbapReadResponse = "00 01 00 00 00 09 01 03 06 02 2B 00 00 00 64"; // V-M-1 应答
    private const string RtuReadResponse = "01 03 02 00 01 79 84";                       // V-R-3

    [Fact(Timeout = 20000)]
    public void TransactionKey_OfTheRequestAndTheResponseIsTheFirstTwoBytes()
    {
        var extractor = new ModbusTransactionKeyExtractor();
        byte[] request = HexVector.ToBytes("00 01 00 00 00 06 01 03 00 6B 00 03"); // V-M-1 请求
        byte[] response = HexVector.ToBytes(MbapReadResponse);

        Assert.True(extractor.TryGetRequestKey(request, out long requestKey));
        Assert.True(extractor.TryGetResponseKey(response, out long responseKey));
        Assert.Equal(1, requestKey);
        Assert.Equal(1, responseKey);
    }

    [Fact(Timeout = 20000)]
    public void TransactionKey_IsReadAsUnsignedSixteenBits()
    {
        var extractor = new ModbusTransactionKeyExtractor();

        Assert.True(extractor.TryGetResponseKey(HexVector.ToBytes("FF FF 00 00 00 03 01 83 02"), out long key));
        Assert.Equal(65535, key);
    }

    [Theory(Timeout = 20000)]
    [InlineData("")]
    [InlineData("01")]
    public void TransactionKey_FewerThanTwoBytes_IsFalse(string hex)
    {
        var extractor = new ModbusTransactionKeyExtractor();
        byte[] bytes = HexVector.ToBytes(hex);

        Assert.False(extractor.TryGetRequestKey(bytes, out _));
        Assert.False(extractor.TryGetResponseKey(bytes, out _));
    }

    [Fact(Timeout = 20000)]
    public void Mbap_DecodesOneFrameFromTheLengthField()
    {
        IFrameDecoder decoder = FrameCodecFactoryFor(ModbusStreamFraming.MbapOptions()).CreateDecoder();
        byte[] frame = HexVector.ToBytes(MbapReadResponse);
        var buffer = new ReadOnlySequence<byte>(frame);

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> decoded));
        Assert.Equal(frame, decoded.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact(Timeout = 20000)]
    public void Mbap_WaitsForTheWholeFrame()
    {
        IFrameDecoder decoder = FrameCodecFactoryFor(ModbusStreamFraming.MbapOptions()).CreateDecoder();
        byte[] frame = HexVector.ToBytes(MbapReadResponse);
        var partial = new ReadOnlySequence<byte>(frame.AsMemory(0, frame.Length - 1));

        Assert.False(decoder.TryDecode(ref partial, out _));
    }

    [Fact(Timeout = 20000)]
    public void Rtu_CodecsDecodeAResponseFrame()
    {
        IFrameDecoder decoder = ModbusStreamFraming.RtuCodecs().CreateDecoder();
        var buffer = new ReadOnlySequence<byte>(HexVector.ToBytes(RtuReadResponse));

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> decoded));
        Assert.Equal(HexVector.ToBytes(RtuReadResponse), decoded.ToArray());
    }

    private static IFrameCodecFactory FrameCodecFactoryFor(FramingOptions options) => FrameCodecFactory.Create(options);
}
