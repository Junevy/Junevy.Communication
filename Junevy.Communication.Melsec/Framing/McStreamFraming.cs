using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Melsec.Protocol;

namespace Junevy.Communication.Melsec.Framing;

/// <summary>
/// 流式传输（TCP、串口）的分帧工厂（P2 计划 Task 11）：3E、4E 用长度字段（偏移为副头部与访问路径等头部的宽度）；
/// 4C 格式 4 用 CR LF；4C 格式 5 用 DLE 边界（<see cref="Mc4CBinaryFrameCodecFactory"/>）。
/// </summary>
internal static class McStreamFraming
{
    private const string CrLf = "\r\n";

    // 4E 在副头部之后多两个定长字段：序列号与固定的 0000（手册 p.42）。
    private const int FourEFixedFieldCount = 2;

    /// <summary>创建流式分帧工厂。</summary>
    /// <param name="settings">选项快照。</param>
    /// <param name="maxFrameLength">单帧的最大字节数；同一个值用于所有分帧配置。</param>
    /// <returns>分帧工厂。</returns>
    public static IFrameCodecFactory Create(McProtocolSettings settings, int maxFrameLength)
    {
        if (settings.Is4C)
        {
            return settings.Encoding == McEncoding.Binary
                ? new Mc4CBinaryFrameCodecFactory(settings.SumCheck, maxFrameLength)
                : FrameCodecFactory.Create(new FramingOptions
                {
                    Mode = FramingMode.Delimiter,
                    Delimiters = new[] { CrLf },
                    KeepDelimiter = false,
                    AppendDelimiterOnSend = true,
                    MaxFrameLength = maxFrameLength,
                });
        }

        McFieldCodec codec = McFieldCodec.For(settings.Encoding);
        int fixedFields = settings.Is4E ? FourEFixedFieldCount : 0;
        int headerLength = codec.UInt16Size + fixedFields * codec.UInt16Size + McAccessRoute.FieldSize(codec);
        return FrameCodecFactory.Create(new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = headerLength,
            LengthFieldSize = codec.UInt16Size,
            LengthFieldEncoding = settings.Encoding == McEncoding.Binary
                ? LengthFieldEncoding.BinaryLittleEndian
                : LengthFieldEncoding.AsciiHex,
            MaxFrameLength = maxFrameLength,
        });
    }
}
