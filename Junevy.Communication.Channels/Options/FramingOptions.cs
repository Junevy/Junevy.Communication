namespace Junevy.Communication.Channels;

/// <summary>
/// 分帧配置。由 <c>FrameCodecFactory.Create</c> 校验并复制；之后修改本对象不影响已创建的工厂与分帧器（D5）。
/// </summary>
public sealed class FramingOptions
{
    /// <summary>分帧模式，默认 <see cref="FramingMode.Raw"/>。</summary>
    public FramingMode Mode { get; set; } = FramingMode.Raw;

    /// <summary>单帧允许的最大长度（字节，含本值），默认 65536。超过时分帧器抛出 <see cref="Framing.FrameDecodeException"/>；Raw 模式按本值切块交出。</summary>
    public int MaxFrameLength { get; set; } = 65536;

    /// <summary>
    /// 分隔符列表（文本或 <c>hex:</c> 格式，见 <c>ByteSequenceParser</c>）；<see cref="FramingMode.Delimiter"/> 模式必填。
    /// </summary>
    public string[]? Delimiters { get; set; }

    /// <summary>是否在交付的帧中保留分隔符。</summary>
    public bool KeepDelimiter { get; set; }

    /// <summary>发送时是否由编码器追加 <c>Delimiters[0]</c>（D18）。</summary>
    public bool AppendDelimiterOnSend { get; set; } = true;

    /// <summary>定长帧的长度（字节）；<see cref="FramingMode.FixedLength"/> 模式必填。</summary>
    public int FrameLength { get; set; }

    /// <summary>长度字段的偏移（字节）；<see cref="FramingMode.LengthField"/> 模式使用。</summary>
    public int LengthFieldOffset { get; set; }

    /// <summary>长度字段的宽度（字节）：二进制为 1、2、4；ASCII 为 1–8。默认 2。</summary>
    public int LengthFieldSize { get; set; } = 2;

    /// <summary>长度字段的编码方式，默认二进制大端。</summary>
    public LengthFieldEncoding LengthFieldEncoding { get; set; } = LengthFieldEncoding.BinaryBigEndian;

    /// <summary>长度修正值：帧总长 = 偏移 + 字段宽度 + 长度值 + 修正值。</summary>
    public int LengthAdjustment { get; set; }

    /// <summary>交付前剥离的字节数（从帧头开始计）。</summary>
    public int InitialBytesToStrip { get; set; }

    /// <summary>起始符（文本或 <c>hex:</c> 格式）；<see cref="FramingMode.StartEnd"/> 模式必填。</summary>
    public string? StartMarker { get; set; }

    /// <summary>结束符（文本或 <c>hex:</c> 格式）；<see cref="FramingMode.StartEnd"/> 模式必填。</summary>
    public string? EndMarker { get; set; }

    /// <summary>交付的帧是否包含起始符与结束符，默认 true。</summary>
    public bool KeepMarkers { get; set; } = true;

    /// <summary>静默判定时间（毫秒），默认 20；<see cref="FramingMode.IdleGap"/> 模式使用。</summary>
    public int GapTimeout { get; set; } = 20;
}
