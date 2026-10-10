using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 分帧器与编解码工厂的测试。六种分帧器共用同一组场景（单帧、粘包、逐字节、多段缓冲）。
/// 没有消息边界的分帧器（Raw、IdleGap）按拼接后的字节比较，因为它们交出的帧数取决于到达方式。
/// </summary>
public sealed class FramingTests
{
    private static readonly int[] SegmentSizes = { 1, 3, 7 };

    /// <summary>六种分帧器的名称（Theory 数据）。</summary>
    public static IEnumerable<object[]> FramerNames { get; } =
        new[] { "Raw", "Delimiter", "FixedLength", "LengthField", "StartEnd", "IdleGap" }.Select(name => new object[] { name });

    /// <summary>LengthField 的真实协议样例名称（Theory 数据）。</summary>
    public static IEnumerable<object[]> ProtocolNames { get; } =
        new[] { "ModbusTcp", "S7Tpkt", "Melsec3EBinary", "Melsec3EAscii", "Melsec4EBinary", "Hsms" }.Select(name => new object[] { name });

    /// <summary>非法配置用例：名称到配置构造函数。</summary>
    private static readonly Dictionary<string, Func<FramingOptions>> InvalidOptionCases = new Dictionary<string, Func<FramingOptions>>
    {
        ["MaxFrameLengthZero"] = () => new FramingOptions { Mode = FramingMode.Raw, MaxFrameLength = 0 },
        ["UnknownMode"] = () => new FramingOptions { Mode = (FramingMode)99 },
        ["DelimiterWithoutDelimiters"] = () => new FramingOptions { Mode = FramingMode.Delimiter },
        ["DelimiterEmptyItem"] = () => new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "" } },
        ["DelimiterInvalidHex"] = () => new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "hex:0G" } },
        ["FixedLengthZero"] = () => new FramingOptions { Mode = FramingMode.FixedLength, FrameLength = 0 },
        ["FixedLengthAboveMax"] = () => new FramingOptions { Mode = FramingMode.FixedLength, FrameLength = 70000 },
        ["LengthFieldBinarySizeThree"] = () => new FramingOptions { Mode = FramingMode.LengthField, LengthFieldSize = 3 },
        ["LengthFieldAsciiSizeNine"] = () => new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldEncoding = LengthFieldEncoding.AsciiHex,
            LengthFieldSize = 9,
        },
        ["LengthFieldNegativeOffset"] = () => new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = -1 },
        ["LengthFieldHeaderBeyondMax"] = () => new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 65535, LengthFieldSize = 4 },
        ["LengthFieldStripBeyondMinimumFrame"] = () => new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = 4,
            LengthFieldSize = 2,
            InitialBytesToStrip = 7,
        },
        ["StartEndWithoutStart"] = () => new FramingOptions { Mode = FramingMode.StartEnd, EndMarker = "\\x03" },
        ["StartEndEmptyEnd"] = () => new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "\\x02", EndMarker = "" },
        ["IdleGapZeroTimeout"] = () => new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = 0 },
    };

    /// <summary>CodecFactory 的非法配置名称（Theory 数据）。</summary>
    public static IEnumerable<object[]> InvalidOptionNames { get; } = InvalidOptionCases.Keys.Select(name => new object[] { name });

    // ————————————————— 六种分帧器共用的场景 —————————————————

    [Theory]
    [MemberData(nameof(FramerNames))]
    public void SingleFrame(string framer)
    {
        FramingFixture fixture = FramingFixtures.Get(framer);
        byte[] payload = fixture.Payload(0);
        byte[] wire = fixture.Encode(payload);

        FeedResult result = DecoderHarness.Feed(fixture.CreateDecoder(), wire, wire.Length);

        AssertDelivered(fixture, result.Frames, new[] { fixture.ExpectedOutput(payload) });
    }

    [Theory]
    [MemberData(nameof(FramerNames))]
    public void HundredFramesCoalesced(string framer)
    {
        FramingFixture fixture = FramingFixtures.Get(framer);
        byte[][] payloads = Payloads(fixture, 100);
        byte[] wire = Wire(fixture, payloads);

        FeedResult result = DecoderHarness.Feed(fixture.CreateDecoder(), wire, wire.Length);

        AssertDelivered(fixture, result.Frames, ExpectedOutputs(fixture, payloads));
    }

    [Theory]
    [MemberData(nameof(FramerNames))]
    public void ByteByByte(string framer)
    {
        FramingFixture fixture = FramingFixtures.Get(framer);
        byte[][] payloads = Payloads(fixture, 100);
        byte[] wire = Wire(fixture, payloads);

        FeedResult result = DecoderHarness.Feed(fixture.CreateDecoder(), wire, 1);

        AssertDelivered(fixture, result.Frames, ExpectedOutputs(fixture, payloads));

        // 逐字节到达时分帧器必须真的被逐字节调用（每个字节至少触发一次 TryDecode）。
        Assert.True(result.TryDecodeCalls >= wire.Length,
            $"Expected at least {wire.Length} TryDecode calls for byte-by-byte input, but observed {result.TryDecodeCalls}.");
    }

    [Theory]
    [MemberData(nameof(FramerNames))]
    public void SegmentedSequence(string framer)
    {
        FramingFixture fixture = FramingFixtures.Get(framer);
        byte[][] payloads = Payloads(fixture, 100);
        byte[] wire = Wire(fixture, payloads);
        IReadOnlyList<byte[]> expected = ExpectedOutputs(fixture, payloads);

        foreach (int segmentSize in SegmentSizes)
        {
            ReadOnlySequence<byte> sequence = SequenceFactory.Segmented(wire, segmentSize);

            // 确认输入确实由多个段组成，否则本测试没有覆盖多段路径。
            int expectedSegments = (wire.Length + segmentSize - 1) / segmentSize;
            Assert.Equal(expectedSegments, SequenceFactory.CountSegments(sequence));

            IReadOnlyList<byte[]> frames = DecoderHarness.DecodeAll(fixture.CreateDecoder(), sequence);
            AssertDelivered(fixture, frames, expected);
        }
    }

    // ————————————————— Delimiter —————————————————

    [Fact]
    public void Delimiter_MultipleDelimiters_EarliestWins()
    {
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n", "\\n" } };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();

        FeedResult result = DecoderHarness.Feed(decoder, Ascii("a\nb\r\nc\n"), 1);

        Assert.Equal(new[] { "a", "b", "c" }, Strings(result.Frames));
    }

    [Fact]
    public void Delimiter_EmptyFramesSkipped()
    {
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();

        FeedResult result = DecoderHarness.Feed(decoder, Ascii("\r\n\r\nx\r\n"), 1);

        Assert.Equal(new[] { "x" }, Strings(result.Frames));
    }

    [Fact]
    public void Delimiter_KeepDelimiter_IncludesDelimiter()
    {
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" }, KeepDelimiter = true };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();

        FeedResult result = DecoderHarness.Feed(decoder, Ascii("a\r\nb\r\n"), 2);

        Assert.Equal(new[] { "a\r\n", "b\r\n" }, Strings(result.Frames));
    }

    [Fact]
    public void Delimiter_TooLong_Throws()
    {
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" }, MaxFrameLength = 16 };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(Enumerable.Repeat((byte)'A', 40).ToArray(), 8);

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    [Fact]
    public void Delimiter_FrameLongerThanMax_Throws()
    {
        // 分隔符已到达，但帧内容超过 MaxFrameLength：同样视为超长（约束适用于所有分帧器）。
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" }, MaxFrameLength = 16 };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(Ascii(new string('B', 30) + "\r\n"), 5);

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    [Fact]
    public void DelimiterEncoder_AppendsFirstDelimiter()
    {
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n", "\\n" } };
        IFrameEncoder encoder = FrameCodecFactory.Create(options).CreateEncoder();
        Assert.Equal("T1\r\n", Encoding.ASCII.GetString(EncodeToArray(encoder, Ascii("T1"))));

        options.AppendDelimiterOnSend = false;
        encoder = FrameCodecFactory.Create(options).CreateEncoder();
        Assert.Equal("T1", Encoding.ASCII.GetString(EncodeToArray(encoder, Ascii("T1"))));
    }

    // ————————————————— LengthField —————————————————

    [Theory]
    [MemberData(nameof(ProtocolNames))]
    public void LengthField_ProtocolTable(string protocol)
    {
        (FramingOptions options, byte[] sample) = LengthFieldProtocol(protocol);

        FeedResult whole = DecoderHarness.Feed(FrameCodecFactory.Create(options).CreateDecoder(), sample, sample.Length);
        Assert.Equal(new[] { sample }, whole.Frames);

        FeedResult bytewise = DecoderHarness.Feed(FrameCodecFactory.Create(options).CreateDecoder(), sample, 1);
        Assert.Equal(new[] { sample }, bytewise.Frames);
    }

    [Fact]
    public void LengthField_AsciiInvalidChar_Throws()
    {
        var options = new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldEncoding = LengthFieldEncoding.AsciiHex,
            LengthFieldSize = 4,
        };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(Ascii("00G8" + "0000"), 2);

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    [Fact]
    public void LengthField_TotalExceedsMax_Throws()
    {
        var options = new FramingOptions { Mode = FramingMode.LengthField, LengthFieldSize = 2, MaxFrameLength = 32 };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(new byte[] { 0x00, 0x64 }); // 长度值 100，总长 102 > 32

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    [Fact]
    public void LengthField_TotalBelowHeader_Throws()
    {
        // 修正值为负：长度值 3 时总长为 2 + 3 - 10 = -5，小于头部。
        var options = new FramingOptions { Mode = FramingMode.LengthField, LengthFieldSize = 2, LengthAdjustment = -10 };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(new byte[] { 0x00, 0x03 });

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    // ————————————————— StartEnd —————————————————

    [Fact]
    public void StartEnd_GarbageBeforeStart_Resyncs()
    {
        var options = new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "\\x02", EndMarker = "\\x03" };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();

        FeedResult result = DecoderHarness.Feed(decoder, new byte[] { (byte)'x', (byte)'x', 0x02, (byte)'A', (byte)'B', 0x03 }, 1);

        Assert.Equal(new[] { new byte[] { 0x02, (byte)'A', (byte)'B', 0x03 } }, result.Frames);
    }

    [Fact]
    public void StartEnd_KeepMarkersFalse_StripsMarkers()
    {
        var options = new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "\\x02", EndMarker = "\\x03", KeepMarkers = false };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();

        FeedResult result = DecoderHarness.Feed(decoder, new byte[] { 0x02, (byte)'A', (byte)'B', 0x03 }, 4);

        Assert.Equal(new[] { "AB" }, Strings(result.Frames));
    }

    // ————————————————— IdleGap —————————————————

    [Fact]
    public void IdleGap_TryFlush_ReturnsBuffer()
    {
        var options = new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = 35 };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        var flushable = Assert.IsAssignableFrom<IFlushableFrameDecoder>(decoder);
        Assert.Equal(35, flushable.FlushTimeout);

        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(Ascii("HELLO"), 2);
        Assert.False(decoder.TryDecode(ref buffer, out _));
        Assert.Equal(5, buffer.Length);

        Assert.True(flushable.TryFlush(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal("HELLO", Encoding.ASCII.GetString(frame.ToArray()));
        Assert.Equal(0, buffer.Length);
        Assert.False(flushable.TryFlush(ref buffer, out _));
    }

    [Fact]
    public void IdleGap_ExactlyMaxFrameLength_IsFlushed()
    {
        // MaxFrameLength 为允许的最大帧长（含）：恰好等于上限的数据不应在 TryDecode 时抛出，静默之后应整体交出。
        var options = new FramingOptions { Mode = FramingMode.IdleGap, MaxFrameLength = 16 };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        var flushable = Assert.IsAssignableFrom<IFlushableFrameDecoder>(decoder);
        byte[] data = Enumerable.Repeat((byte)'A', 16).ToArray();
        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(data, 4);

        Assert.False(decoder.TryDecode(ref buffer, out _));
        Assert.Equal(16, buffer.Length);

        Assert.True(flushable.TryFlush(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(data, frame.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void IdleGap_MaxFrameLengthPlusOne_Throws()
    {
        var options = new FramingOptions { Mode = FramingMode.IdleGap, MaxFrameLength = 16 };
        IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(Enumerable.Repeat((byte)'A', 17).ToArray(), 5);

        Assert.Throws<FrameDecodeException>(() => { decoder.TryDecode(ref buffer, out _); });
    }

    // ————————————————— 工厂校验与配置快照 —————————————————

    [Theory]
    [MemberData(nameof(InvalidOptionNames))]
    public void CodecFactory_InvalidOptions_Throw(string caseName)
    {
        FramingOptions options = InvalidOptionCases[caseName]();

        Assert.ThrowsAny<ArgumentException>(() => { FrameCodecFactory.Create(options); });
    }

    [Fact]
    public void CodecFactory_NullOptions_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => { FrameCodecFactory.Create(null!); });
    }

    [Fact]
    public void Create_SnapshotsOptions_LaterChangesDoNotAffectFactory()
    {
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\n" } };
        IFrameCodecFactory factory = FrameCodecFactory.Create(options);

        options.Mode = FramingMode.Raw;
        options.Delimiters![0] = "\\r";

        FeedResult result = DecoderHarness.Feed(factory.CreateDecoder(), Ascii("a\nb\n"), 4);

        Assert.Equal(new[] { "a", "b" }, Strings(result.Frames));
        Assert.Equal(FramingMode.Raw, options.Mode);
    }

    // ————————————————— 字节序列文本解析 —————————————————

    [Fact]
    public void ByteSequenceParser_Cases()
    {
        Assert.Equal(new byte[] { 0x0D, 0x0A }, ByteSequenceParser.Parse("hex:0D0A"));
        Assert.Equal(new byte[] { 0x0D, 0x0A }, ByteSequenceParser.Parse("hex:0D-0A"));
        Assert.Equal(new byte[] { 0x0D, 0x0A }, ByteSequenceParser.Parse("hex:0D 0A"));
        Assert.Equal(new byte[] { 0x0D, 0x0A }, ByteSequenceParser.Parse("\\r\\n"));
        Assert.Equal(new byte[] { 0x02 }, ByteSequenceParser.Parse("\\x02"));
        Assert.Equal(new byte[] { 0x5C }, ByteSequenceParser.Parse("\\\\"));
        Assert.Equal(new byte[] { 0x41, 0x09, 0x00 }, ByteSequenceParser.Parse("A\\t\\0"));
        Assert.Equal(Encoding.UTF8.GetBytes("é"), ByteSequenceParser.Parse("é"));
        Assert.Throws<FormatException>(() => ByteSequenceParser.Parse("hex:0G"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hex:")]
    [InlineData("hex:0")]
    [InlineData("\\q")]
    [InlineData("abc\\")]
    [InlineData("\\x4")]
    public void ByteSequenceParser_InvalidText_ThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => ByteSequenceParser.Parse(text));
    }

    // ————————————————— 辅助方法 —————————————————

    /// <summary>
    /// 六种分帧器共用的断言。没有消息边界的分帧器比较拼接后的字节，其余比较帧序列。
    /// </summary>
    private static void AssertDelivered(FramingFixture fixture, IReadOnlyList<byte[]> actual, IReadOnlyList<byte[]> expected)
    {
        if (fixture.StreamSemantics)
        {
            Assert.Equal(Join(expected), Join(actual));
            return;
        }

        Assert.Equal(expected, actual);
    }

    private static byte[][] Payloads(FramingFixture fixture, int count)
        => Enumerable.Range(0, count).Select(i => fixture.Payload(i)).ToArray();

    private static byte[] Wire(FramingFixture fixture, IEnumerable<byte[]> payloads)
        => Join(payloads.Select(payload => fixture.Encode(payload)));

    private static byte[][] ExpectedOutputs(FramingFixture fixture, IEnumerable<byte[]> payloads)
        => payloads.Select(payload => fixture.ExpectedOutput(payload)).ToArray();

    private static byte[] Join(IEnumerable<byte[]> parts)
        => parts.SelectMany(part => part).ToArray();

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>用管道作为 <see cref="IBufferWriter{T}"/> 收集编码结果。</summary>
    private static byte[] EncodeToArray(IFrameEncoder encoder, byte[] payload)
    {
        var pipe = new Pipe();
        encoder.Encode(payload, pipe.Writer);
        pipe.Writer.FlushAsync().GetAwaiter().GetResult();

        pipe.Reader.TryRead(out ReadResult result);
        byte[] bytes = result.Buffer.ToArray();
        pipe.Reader.AdvanceTo(result.Buffer.End);
        pipe.Writer.Complete();
        pipe.Reader.Complete();
        return bytes;
    }

    private static List<string> Strings(IEnumerable<byte[]> frames)
        => frames.Select(frame => Encoding.ASCII.GetString(frame)).ToList();

    /// <summary>
    /// 设计文档 5.2 节的六个真实协议样例：返回对应的 LengthField 配置与一帧完整的线路字节。
    /// </summary>
    private static (FramingOptions Options, byte[] Sample) LengthFieldProtocol(string protocol)
    {
        switch (protocol)
        {
            case "ModbusTcp":
                // 事务标识、协议标识、长度（第 4–5 字节，大端，计其后字节数）。
                return (
                    new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 4, LengthFieldSize = 2 },
                    new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 });

            case "S7Tpkt":
                // TPKT：版本、保留、长度（第 2–3 字节，大端，含 4 字节头）。
                return (
                    new FramingOptions
                    {
                        Mode = FramingMode.LengthField,
                        LengthFieldOffset = 2,
                        LengthFieldSize = 2,
                        LengthAdjustment = -4,
                    },
                    new byte[]
                    {
                        0x03, 0x00, 0x00, 0x16, 0x02, 0xF0, 0x80, 0x32, 0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x08, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x01, 0x00,
                    });

            case "Melsec3EBinary":
                // MC 3E 二进制应答：长度位于第 7–8 字节，小端；长度含结束码与数据。
                return (
                    new FramingOptions
                    {
                        Mode = FramingMode.LengthField,
                        LengthFieldOffset = 7,
                        LengthFieldSize = 2,
                        LengthFieldEncoding = LengthFieldEncoding.BinaryLittleEndian,
                    },
                    new byte[] { 0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x12, 0x34 });

            case "Melsec3EAscii":
                // MC 3E ASCII 应答：第 14–17 个字符为十六进制长度（4 个字符）。
                return (
                    new FramingOptions
                    {
                        Mode = FramingMode.LengthField,
                        LengthFieldOffset = 14,
                        LengthFieldSize = 4,
                        LengthFieldEncoding = LengthFieldEncoding.AsciiHex,
                    },
                    Ascii("D000" + "00" + "FF" + "03FF" + "00" + "0008" + "00001234"));

            case "Melsec4EBinary":
                // MC 4E 二进制应答：长度位于第 11–12 字节，小端。
                return (
                    new FramingOptions
                    {
                        Mode = FramingMode.LengthField,
                        LengthFieldOffset = 11,
                        LengthFieldSize = 2,
                        LengthFieldEncoding = LengthFieldEncoding.BinaryLittleEndian,
                    },
                    new byte[]
                    {
                        0xD4, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00,
                        0x02, 0x00, 0x00, 0x00,
                    });

            case "Hsms":
                // SECS HSMS：前 4 字节为长度（大端），不包含自身，之后是 10 字节消息头。
                return (
                    new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 0, LengthFieldSize = 4 },
                    new byte[] { 0x00, 0x00, 0x00, 0x0A, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x01 });

            default:
                throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown protocol sample.");
        }
    }
}
