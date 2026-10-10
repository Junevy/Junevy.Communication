using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Melsec.Framing;
using Junevy.Communication.Tcp;
using static Junevy.Communication.Melsec.Tests.McTestSupport;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 协议配置 <see cref="McProtocolProfile"/>（计划 Task 11）：组件属性已由调用方设置时拒绝（不静默覆盖）；
/// 按帧类型设置分帧、关联与键提取器；各帧的分帧切分（长度字段、CR LF、DLE 边界）；4E 的关联键。
/// </summary>
public sealed class McProtocolProfileTests
{
    [Theory(Timeout = 20000)]
    [InlineData(McFrameType.Frame3E, McEncoding.Binary, CorrelationMode.Sequential, false)]
    [InlineData(McFrameType.Frame3E, McEncoding.Ascii, CorrelationMode.Sequential, false)]
    [InlineData(McFrameType.Frame4E, McEncoding.Binary, CorrelationMode.Keyed, true)]
    [InlineData(McFrameType.Frame4E, McEncoding.Ascii, CorrelationMode.Keyed, true)]
    [InlineData(McFrameType.Frame4C, McEncoding.Binary, CorrelationMode.Sequential, false)]
    [InlineData(McFrameType.Frame4C, McEncoding.Ascii, CorrelationMode.Sequential, false)]
    public void ConfigureStream_SetsFramingCorrelationKeyExtractorAndProbeFactory(
        McFrameType frameType, McEncoding encoding, CorrelationMode correlation, bool hasKeyExtractor)
    {
        var components = McProtocolProfile.ConfigureStream(
            new TcpChannelComponents(), new McClientOptions { FrameType = frameType, Encoding = encoding });

        Assert.NotNull(components.FrameCodec);
        Assert.Equal(correlation, components.Correlation);
        Assert.Equal(hasKeyExtractor, components.KeyExtractor != null);
        Assert.NotNull(components.HealthProbeFactory);
    }

    [Fact(Timeout = 20000)]
    public void ConfigureStream_RejectsAFrameCodecSetByTheCaller()
    {
        var components = new ChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        Assert.Throws<ArgumentException>(() => { McProtocolProfile.ConfigureStream(components, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void ConfigureStream_RejectsACorrelationSetByTheCaller()
    {
        var components = new ChannelComponents { Correlation = CorrelationMode.Sequential };

        Assert.Throws<ArgumentException>(() => { McProtocolProfile.ConfigureStream(components, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void ConfigureStream_RejectsAKeyExtractorSetByTheCaller()
    {
        var components = new ChannelComponents { KeyExtractor = new McSerialKeyExtractor(McEncoding.Binary) };

        Assert.Throws<ArgumentException>(() => { McProtocolProfile.ConfigureStream(components, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void ConfigureStream_RejectsAHealthProbeSetByTheCaller()
    {
        var components = new ChannelComponents { HealthProbe = new NoProbe() };

        Assert.Throws<ArgumentException>(() => { McProtocolProfile.ConfigureStream(components, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void ConfigureStream_RejectsAHealthProbeFactorySetByTheCaller()
    {
        var components = new ChannelComponents { HealthProbeFactory = _ => new NoProbe() };

        Assert.Throws<ArgumentException>(() => { McProtocolProfile.ConfigureStream(components, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void ConfigureDatagram_RejectsACorrelationSetByTheCaller()
    {
        var components = new ChannelComponents { Correlation = CorrelationMode.Sequential };

        Assert.Throws<ArgumentException>(() => { McProtocolProfile.ConfigureDatagram(components, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void ConfigureDatagram_RejectsFrame4C_BecauseDatagramsAreEthernetOnly()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            McProtocolProfile.ConfigureDatagram(new ChannelComponents(), new McClientOptions { FrameType = McFrameType.Frame4C });
        });
    }

    [Fact(Timeout = 20000)]
    public void ConfigureDatagram_SetsKeyedCorrelationForFrame4E_WithoutFraming()
    {
        var components = McProtocolProfile.ConfigureDatagram(new ChannelComponents(), new McClientOptions { FrameType = McFrameType.Frame4E });

        Assert.Null(components.FrameCodec);
        Assert.Equal(CorrelationMode.Keyed, components.Correlation);
        Assert.NotNull(components.KeyExtractor);
        Assert.NotNull(components.HealthProbeFactory);
    }

    [Fact(Timeout = 20000)]
    public void Framing3EBinary_SplitsTheResponseAtTheLengthField()
    {
        // V1 的应答：长度字段位于偏移 7（副头部 2 字节 + 访问路径 5 字节）。
        AssertFirstFrame(new McClientOptions { FrameType = McFrameType.Frame3E }, Hex("D0 00 00 FF FF 03 00 08 00 00 00 34 12 02 00 EF 1D"), Hex("D0 00"));
    }

    [Fact(Timeout = 20000)]
    public void Framing3EAscii_SplitsTheResponseAtTheLengthField()
    {
        // V2 的应答：长度字段位于偏移 14（副头部 4 字符 + 访问路径 10 字符）。
        AssertFirstFrame(
            new McClientOptions { FrameType = McFrameType.Frame3E, Encoding = McEncoding.Ascii },
            Text("D000" + "00FF03FF00" + "0010" + "0000" + "123400021DEF"),
            Text("D000"));
    }

    [Fact(Timeout = 20000)]
    public void Framing4EBinary_SplitsTheResponseAtTheLengthField()
    {
        // V3 的应答：长度字段位于偏移 11（副头部 2、序列号 2、固定 2、访问路径 5 字节）。
        AssertFirstFrame(
            new McClientOptions { FrameType = McFrameType.Frame4E },
            Hex("D4 00 34 12 00 00 00 FF FF 03 00 06 00 00 00 00 01 00 11"),
            Hex("D4 00"));
    }

    [Fact(Timeout = 20000)]
    public void Framing4EAscii_SplitsTheResponseAtTheLengthField()
    {
        // V4 的应答：长度字段位于偏移 22（4 个字符的副头部、序列号、固定字段，以及 10 个字符的访问路径）。
        AssertFirstFrame(
            new McClientOptions { FrameType = McFrameType.Frame4E, Encoding = McEncoding.Ascii },
            Text("D400" + "1234" + "0000" + "00FF03FF00" + "0004" + "0000"),
            Text("D400"));
    }

    [Fact(Timeout = 20000)]
    public void Format4_SplitsAtCrLf_AndDeliversTheFrameWithoutIt()
    {
        // 格式 4 的 NAK 应答（V10），通道去掉 CR LF 后交给协议层。
        byte[] frame = Join(Hex("15"), Text("F80000FF03FF0000" + "C051"));
        var components = McProtocolProfile.ConfigureStream(
            new ChannelComponents(), new McClientOptions { FrameType = McFrameType.Frame4C, Encoding = McEncoding.Ascii });
        IFrameDecoder decoder = components.FrameCodec!.CreateDecoder();
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Join(frame, Hex("0D 0A")));

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> first));
        Assert.Equal(frame, first.ToArray());
    }

    [Fact(Timeout = 20000)]
    public void Format5_SplitsAtTheDleEtxAndItsChecksum()
    {
        // V8 的应答（含附加码与和校验）；后面的部分帧以 DLE STX 开始。
        AssertFirstFrame(
            new McClientOptions { FrameType = McFrameType.Frame4C },
            Hex("10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03 33 37"),
            Hex("10 02"));
    }

    [Fact(Timeout = 20000)]
    public void KeyExtractor_Binary4E_ReadsTheSerialFromRequestAndResponse()
    {
        var extractor = new McSerialKeyExtractor(McEncoding.Binary);

        Assert.True(extractor.TryGetRequestKey(Hex("54 00 34 12 00 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 90 08 00"), out long requestKey));
        Assert.Equal(0x1234L, requestKey);
        Assert.True(extractor.TryGetResponseKey(Hex("D4 00 34 12 00 00 00 FF FF 03 00 06 00 00 00 00 01 00 11"), out long responseKey));
        Assert.Equal(0x1234L, responseKey);
    }

    [Fact(Timeout = 20000)]
    public void KeyExtractor_Ascii4E_ReadsTheSerialFromTheFourHexCharacters()
    {
        var extractor = new McSerialKeyExtractor(McEncoding.Ascii);

        Assert.True(extractor.TryGetRequestKey(Text("5400" + "1234" + "0000" + "00FF03FF00" + "0024" + "0010" + "14010000D*0001000003199512021130"), out long key));
        Assert.Equal(0x1234L, key);
    }

    private static void AssertFirstFrame(McClientOptions options, byte[] frame, byte[] partialNext)
    {
        var components = McProtocolProfile.ConfigureStream(new ChannelComponents(), options);
        IFrameDecoder decoder = components.FrameCodec!.CreateDecoder();
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Join(frame, partialNext));

        Assert.True(decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> first));
        Assert.Equal(frame, first.ToArray());
        Assert.Equal(partialNext.Length, buffer.Length);
    }

    private sealed class NoProbe : IHealthProbe
    {
        public Task<Junevy.Communication.Core.Results.CommResult> ProbeAsync(CancellationToken cancellationToken)
            => Task.FromResult(Junevy.Communication.Core.Results.CommResult.Success());
    }
}
