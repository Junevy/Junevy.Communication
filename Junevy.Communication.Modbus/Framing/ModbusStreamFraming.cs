using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Modbus.Framing;

/// <summary>
/// 字节流上两种帧格式的装配（P7-D8）：MBAP 用长度字段分帧，由套件的 <see cref="FrameCodecFactory"/> 完成；RTU 用功能码分帧，由本包的解码器完成。
/// 数据报（UDP）不设分帧，一个数据报就是一帧。
/// </summary>
internal static class ModbusStreamFraming
{
    // MBAP：事务号 2 字节、协议号 2 字节之后是长度字段（2 字节）。长度字段的值是站号与 PDU 的字节数，因此修正值为 0。
    private const int LengthFieldOffset = 4;
    private const int LengthFieldSize = 2;
    private const int MaxMbapFrameLength = 260;

    /// <summary>MBAP 的长度字段分帧选项（Modbus TCP）。</summary>
    public static FramingOptions MbapOptions() => new FramingOptions
    {
        Mode = FramingMode.LengthField,
        LengthFieldOffset = LengthFieldOffset,
        LengthFieldSize = LengthFieldSize,
        LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
        LengthAdjustment = 0,
        MaxFrameLength = MaxMbapFrameLength,
    };

    /// <summary>RTU 的编解码工厂（串口与 RTU over TCP）。</summary>
    public static IFrameCodecFactory RtuCodecs() => new ModbusRtuFrameCodecFactory();
}
