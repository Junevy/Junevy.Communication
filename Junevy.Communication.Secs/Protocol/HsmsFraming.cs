using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// HSMS 的分帧声明（P51-D6）：偏移 0、宽 4、大端、修正值 0——长度字段就是整条报文的长度减去它自己的 4 字节。
/// </summary>
/// <remarks>
/// 长度字段里的值不含自身，因此修正值为 0，剥��字节数也是 0：**帧交给协议层时仍带着那 4 字节长度**，
/// 取关联键与写日志都按完整的报文看。编码侧是 <c>PassthroughFrameEncoder</c>，长度字段由
/// <see cref="HsmsMessage"/> 自己写——通道不替协议加任何字节。
/// 最大帧长取 <c>MaxMessageLength</c>（P51-D5），同一份配置同时限制分帧与正文解码。
/// </remarks>
internal static class HsmsFraming
{
    /// <summary>创建按 HSMS 长度字段分帧的编解码工厂。</summary>
    /// <param name="maxFrameLength">最大帧长（字节），等于 <c>MaxMessageLength</c>。</param>
    /// <returns>分帧工厂；每条连接用自己的分帧器。</returns>
    public static IFrameCodecFactory Create(int maxFrameLength)
        => FrameCodecFactory.Create(new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = 0,
            LengthFieldSize = HsmsMessage.LengthPrefixBytes,
            LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
            LengthAdjustment = 0,
            MaxFrameLength = maxFrameLength,
        });
}