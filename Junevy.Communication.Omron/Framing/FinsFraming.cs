using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Omron.Framing;

/// <summary>
/// FINS/TCP 分帧（计划 Task 13 第 1 条）：外壳的长度字段在偏移 4、宽 4、大端，声明值不含魔数与自身，
/// 因此修正值为 0（帧总长 = 4 + 4 + 长度值）。分帧器由 <see cref="FrameCodecFactory"/> 按声明生成，协议包不自己解码；
/// 最大帧长取 <see cref="FramingOptions.DefaultMaxFrameLength"/>，与 S7 同口径，超过即由分帧器抛出 <see cref="FrameDecodeException"/>。
/// </summary>
internal static class FinsFraming
{
    // 外壳为 "FINS" 4 + 长度 4（第 17.2 节说明段）。
    private const int LengthFieldOffset = 4;
    private const int LengthFieldSize = 4;

    /// <summary>创建按 FINS/TCP 长度分帧的编解码工厂。</summary>
    /// <returns>分帧工厂；每条连接用自己的分帧器。</returns>
    public static IFrameCodecFactory Create()
        => FrameCodecFactory.Create(new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = LengthFieldOffset,
            LengthFieldSize = LengthFieldSize,
            LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
            LengthAdjustment = 0,
            MaxFrameLength = FramingOptions.DefaultMaxFrameLength,
        });
}
