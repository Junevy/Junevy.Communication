using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 一种分帧器的测试夹具：分帧配置、单帧的线路编码，以及该帧交付给上层的内容。
/// </summary>
internal sealed class FramingFixture
{
    private readonly int fixedPayloadLength;
    private readonly Func<byte[], byte[]> encode;
    private readonly Func<byte[], byte[]> expectedOutput;

    public FramingFixture(string name, FramingOptions options, bool streamSemantics, int fixedPayloadLength,
                          Func<byte[], byte[]> encode, Func<byte[], byte[]> expectedOutput)
    {
        Name = name;
        Options = options;
        StreamSemantics = streamSemantics;
        this.fixedPayloadLength = fixedPayloadLength;
        this.encode = encode;
        this.expectedOutput = expectedOutput;
    }

    /// <summary>分帧器名称。</summary>
    public string Name { get; }

    /// <summary>分帧配置。</summary>
    public FramingOptions Options { get; }

    /// <summary>
    /// 没有消息边界的分帧器（Raw、IdleGap）：交出的帧数取决于到达方式，断言只比较拼接后的字节。
    /// </summary>
    public bool StreamSemantics { get; }

    /// <summary>创建一个新的分帧器。</summary>
    public IFrameDecoder CreateDecoder() => FrameCodecFactory.Create(Options).CreateDecoder();

    /// <summary>把一个负载编码为线路上的一帧。</summary>
    public byte[] Encode(byte[] payload) => encode(payload);

    /// <summary>负载对应的交付内容。</summary>
    public byte[] ExpectedOutput(byte[] payload) => expectedOutput(payload);

    /// <summary>生成确定性的负载：不包含分隔符字节，长度在 3–11 之间变化（定长夹具除外）。</summary>
    public byte[] Payload(int index)
    {
        int length = fixedPayloadLength > 0 ? fixedPayloadLength : 3 + index % 9;
        var payload = new byte[length];
        for (int k = 0; k < length; k++)
            payload[k] = (byte)('A' + (index + k) % 26);

        return payload;
    }

    public override string ToString() => Name;
}

/// <summary>
/// 六种分帧器的测试夹具集合。
/// </summary>
internal static class FramingFixtures
{
    private static readonly byte[] CrLf = { 0x0D, 0x0A };
    private static readonly byte[] Stx = { 0x02 };
    private static readonly byte[] Etx = { 0x03 };

    /// <summary>按名称获取夹具：Raw、Delimiter、FixedLength、LengthField、StartEnd、IdleGap。</summary>
    /// <param name="name">分帧器名称。</param>
    /// <returns>夹具。</returns>
    public static FramingFixture Get(string name)
    {
        switch (name)
        {
            case "Raw":
                return new FramingFixture(name, new FramingOptions { Mode = FramingMode.Raw },
                    streamSemantics: true, fixedPayloadLength: 0, encode: Identity, expectedOutput: Identity);

            case "Delimiter":
                return new FramingFixture(name, new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } },
                    streamSemantics: false, fixedPayloadLength: 0, encode: p => Join(p, CrLf), expectedOutput: Identity);

            case "FixedLength":
                return new FramingFixture(name, new FramingOptions { Mode = FramingMode.FixedLength, FrameLength = 8 },
                    streamSemantics: false, fixedPayloadLength: 8, encode: Identity, expectedOutput: Identity);

            case "LengthField":
                // 两字节大端长度头（偏移 0），交付时剥离头部，只剩负载。
                return new FramingFixture(name,
                    new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 0, LengthFieldSize = 2, InitialBytesToStrip = 2 },
                    streamSemantics: false, fixedPayloadLength: 0,
                    encode: p => Join(new[] { (byte)(p.Length >> 8), (byte)p.Length }, p),
                    expectedOutput: Identity);

            case "StartEnd":
                return new FramingFixture(name, new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "\\x02", EndMarker = "\\x03" },
                    streamSemantics: false, fixedPayloadLength: 0, encode: p => Join(Stx, p, Etx), expectedOutput: p => Join(Stx, p, Etx));

            case "IdleGap":
                return new FramingFixture(name, new FramingOptions { Mode = FramingMode.IdleGap },
                    streamSemantics: true, fixedPayloadLength: 0, encode: Identity, expectedOutput: Identity);

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown framer.");
        }
    }

    private static byte[] Identity(byte[] payload) => payload;

    private static byte[] Join(byte[] first, byte[] second) => Join(new[] { first, second });

    private static byte[] Join(byte[] first, byte[] second, byte[] third) => Join(new[] { first, second, third });

    private static byte[] Join(IEnumerable<byte[]> parts) => parts.SelectMany(part => part).ToArray();
}
