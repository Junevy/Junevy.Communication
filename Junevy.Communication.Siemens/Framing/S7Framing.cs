using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Siemens.Framing;

/// <summary>
/// TPKT 分帧（设计 5.2 的 S7 行、计划第 12 节第 1 条）：偏移 2、宽度 2、大端，长度含 TPKT 自身因此修正值为 −4。
/// 分帧器由 <see cref="FrameCodecFactory"/> 按声明生成，协议包不自己解码；最大帧长取 <see cref="FramingOptions.DefaultMaxFrameLength"/>，
/// 覆盖 TPKT 长度字段所能表示的 65535 字节。
/// </summary>
internal static class S7Framing
{
    // TPKT 为 03 00 + 2 字节大端总长（第 17.1 节说明段）。
    private const int LengthFieldOffset = 2;
    private const int LengthFieldSize = 2;
    private const int TpktHeaderBytes = 4;

    /// <summary>创建按 TPKT 长度分帧的编解码工厂。</summary>
    /// <returns>分帧工厂；每条连接用自己的分帧器。</returns>
    public static IFrameCodecFactory Create()
        => FrameCodecFactory.Create(new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = LengthFieldOffset,
            LengthFieldSize = LengthFieldSize,
            LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
            LengthAdjustment = -TpktHeaderBytes,
            MaxFrameLength = FramingOptions.DefaultMaxFrameLength,
        });
}
