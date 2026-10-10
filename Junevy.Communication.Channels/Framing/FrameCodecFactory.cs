namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// 分帧编解码工厂。<see cref="Create"/> 校验 <see cref="FramingOptions"/>（D5：非法参数抛出 <see cref="ArgumentException"/> 族，
/// 不修改调用方的对象），并复制配置的值；之后修改调用方的对象不影响已返回的工厂。
/// </summary>
public static class FrameCodecFactory
{
    /// <summary>
    /// 校验配置并创建编解码工厂。
    /// </summary>
    /// <param name="options">分帧配置；不能为 null。</param>
    /// <returns>按配置创建分帧器与编码器的工厂。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public static IFrameCodecFactory Create(FramingOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        FramingOptions snapshot = Snapshot(options);
        ValidateCommon(snapshot);

        switch (snapshot.Mode)
        {
            case FramingMode.Raw:
                return new CodecFactory(snapshot, Array.Empty<BytePattern>(), null, null);

            case FramingMode.Delimiter:
                return new CodecFactory(snapshot, BuildDelimiters(snapshot), null, null);

            case FramingMode.FixedLength:
                ValidateFixedLength(snapshot);
                return new CodecFactory(snapshot, Array.Empty<BytePattern>(), null, null);

            case FramingMode.LengthField:
                ValidateLengthField(snapshot);
                return new CodecFactory(snapshot, Array.Empty<BytePattern>(), null, null);

            case FramingMode.StartEnd:
                BytePattern startMarker = BuildPattern(snapshot.StartMarker, nameof(FramingOptions.StartMarker));
                BytePattern endMarker = BuildPattern(snapshot.EndMarker, nameof(FramingOptions.EndMarker));
                return new CodecFactory(snapshot, Array.Empty<BytePattern>(), startMarker, endMarker);

            case FramingMode.IdleGap:
                ValidateIdleGap(snapshot);
                return new CodecFactory(snapshot, Array.Empty<BytePattern>(), null, null);

            default:
                throw new ArgumentException($"Unsupported framing mode '{snapshot.Mode}'.", nameof(FramingOptions.Mode));
        }
    }

    private static FramingOptions Snapshot(FramingOptions source)
        => new FramingOptions
        {
            Mode = source.Mode,
            MaxFrameLength = source.MaxFrameLength,
            Delimiters = source.Delimiters == null ? null : (string[])source.Delimiters.Clone(),
            KeepDelimiter = source.KeepDelimiter,
            AppendDelimiterOnSend = source.AppendDelimiterOnSend,
            FrameLength = source.FrameLength,
            LengthFieldOffset = source.LengthFieldOffset,
            LengthFieldSize = source.LengthFieldSize,
            LengthFieldEncoding = source.LengthFieldEncoding,
            LengthAdjustment = source.LengthAdjustment,
            InitialBytesToStrip = source.InitialBytesToStrip,
            StartMarker = source.StartMarker,
            EndMarker = source.EndMarker,
            KeepMarkers = source.KeepMarkers,
            GapTimeout = source.GapTimeout,
        };

    private static void ValidateCommon(FramingOptions options)
    {
        if (!Enum.IsDefined(typeof(FramingMode), options.Mode))
            throw new ArgumentException($"Unsupported framing mode '{options.Mode}'.", nameof(FramingOptions.Mode));

        if (options.MaxFrameLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.MaxFrameLength), options.MaxFrameLength, "MaxFrameLength must be greater than zero.");
    }

    private static void ValidateFixedLength(FramingOptions options)
    {
        if (options.FrameLength <= 0 || options.FrameLength > options.MaxFrameLength)
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.FrameLength), options.FrameLength,
                "FrameLength must be within [1, MaxFrameLength].");
    }

    private static void ValidateLengthField(FramingOptions options)
    {
        if (options.LengthFieldOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.LengthFieldOffset), options.LengthFieldOffset,
                "LengthFieldOffset must not be negative.");

        if (!Enum.IsDefined(typeof(LengthFieldEncoding), options.LengthFieldEncoding))
            throw new ArgumentException($"Unsupported length field encoding '{options.LengthFieldEncoding}'.", nameof(FramingOptions.LengthFieldEncoding));

        bool binary = options.LengthFieldEncoding == LengthFieldEncoding.BinaryBigEndian
                      || options.LengthFieldEncoding == LengthFieldEncoding.BinaryLittleEndian;
        int size = options.LengthFieldSize;
        bool sizeValid = binary
            ? (size == 1 || size == 2 || size == 4)
            : (size >= 1 && size <= 8);
        if (!sizeValid)
        {
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.LengthFieldSize), size,
                binary ? "A binary LengthFieldSize must be 1, 2 or 4." : "An ASCII LengthFieldSize must be within [1, 8].");
        }

        if (options.InitialBytesToStrip < 0)
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.InitialBytesToStrip), options.InitialBytesToStrip,
                "InitialBytesToStrip must not be negative.");

        if ((long)options.LengthFieldOffset + size > options.MaxFrameLength)
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.LengthFieldOffset), options.LengthFieldOffset,
                "The length field must end within MaxFrameLength.");

        // 可接受的最短帧：总长 ≥ 偏移 + 字段宽度（否则解码时抛出），且长度值非负，因此总长 ≥ 偏移 + 字段宽度 + max(修正值, 0)。
        // 被剥离的字节必须落在这个最短帧之内，解码时不再需要逐帧检查。
        long minimumFrame = (long)options.LengthFieldOffset + size + Math.Max(options.LengthAdjustment, 0);
        if (options.InitialBytesToStrip > minimumFrame)
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.InitialBytesToStrip), options.InitialBytesToStrip,
                "InitialBytesToStrip exceeds the smallest accepted frame (LengthFieldOffset + LengthFieldSize + max(LengthAdjustment, 0)).");
    }

    private static void ValidateIdleGap(FramingOptions options)
    {
        if (options.GapTimeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(FramingOptions.GapTimeout), options.GapTimeout, "GapTimeout must be greater than zero.");
    }

    private static BytePattern[] BuildDelimiters(FramingOptions options)
    {
        if (options.Delimiters == null || options.Delimiters.Length == 0)
            throw new ArgumentException("Delimiter mode requires at least one delimiter.", nameof(FramingOptions.Delimiters));

        var patterns = new BytePattern[options.Delimiters.Length];
        for (int i = 0; i < patterns.Length; i++)
            patterns[i] = BuildPattern(options.Delimiters[i], nameof(FramingOptions.Delimiters));

        return patterns;
    }

    private static BytePattern BuildPattern(string? text, string parameterName)
    {
        if (text == null || text.Length == 0)
            throw new ArgumentException($"{parameterName} must not contain null or empty byte sequences.", parameterName);

        try
        {
            return new BytePattern(ByteSequenceParser.Parse(text));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Invalid byte sequence '{text}' in {parameterName}: {ex.Message}", parameterName, ex);
        }
    }

    /// <summary>
    /// 已校验的配置快照及其派生对象（解析后的模式）；每次 <see cref="CreateDecoder"/> 按快照创建新的分帧器。
    /// </summary>
    private sealed class CodecFactory : IFrameCodecFactory
    {
        private readonly FramingOptions options;
        private readonly BytePattern[] delimiters;
        private readonly BytePattern? startMarker;
        private readonly BytePattern? endMarker;
        private readonly IFrameEncoder encoder;

        public CodecFactory(FramingOptions options, BytePattern[] delimiters, BytePattern? startMarker, BytePattern? endMarker)
        {
            this.options = options;
            this.delimiters = delimiters;
            this.startMarker = startMarker;
            this.endMarker = endMarker;

            encoder = options.Mode == FramingMode.Delimiter
                ? new DelimiterFrameEncoder(options.AppendDelimiterOnSend ? delimiters[0].Bytes : null)
                : PassthroughFrameEncoder.Instance;
        }

        public IFrameDecoder CreateDecoder()
        {
            switch (options.Mode)
            {
                case FramingMode.Raw:
                    return new RawFrameDecoder(options.MaxFrameLength);

                case FramingMode.Delimiter:
                    return new DelimiterFrameDecoder(delimiters, options.KeepDelimiter, options.MaxFrameLength);

                case FramingMode.FixedLength:
                    return new FixedLengthFrameDecoder(options.FrameLength);

                case FramingMode.LengthField:
                    return new LengthFieldFrameDecoder(options.LengthFieldOffset, options.LengthFieldSize, options.LengthFieldEncoding,
                        options.LengthAdjustment, options.InitialBytesToStrip, options.MaxFrameLength);

                case FramingMode.StartEnd:
                    return new StartEndFrameDecoder(startMarker!, endMarker!, options.KeepMarkers, options.MaxFrameLength);

                case FramingMode.IdleGap:
                    return new IdleGapFrameDecoder(options.GapTimeout, options.MaxFrameLength);

                default:
                    throw new InvalidOperationException($"Unsupported framing mode '{options.Mode}'.");
            }
        }

        public IFrameEncoder CreateEncoder() => encoder;
    }
}
