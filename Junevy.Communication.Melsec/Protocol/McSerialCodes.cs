namespace Junevy.Communication.Melsec.Protocol;

/// <summary>串口 4C 帧的控制码、帧识别号与应答识别号（手册 p.32–37）。</summary>
internal static class McSerialCodes
{
    /// <summary>格式 4 请求的起始控制码 ENQ（手册 p.32）。</summary>
    public const byte Enq = 0x05;

    /// <summary>格式 4 数据应答的起始控制码 STX（手册 p.32）；格式 5 的 DLE STX 的第二字节（手册 p.33）。</summary>
    public const byte Stx = 0x02;

    /// <summary>格式 4 数据应答的结束控制码 ETX（手册 p.32）；格式 5 的 DLE ETX 的第二字节（手册 p.33）。</summary>
    public const byte Etx = 0x03;

    /// <summary>格式 4 无数据应答的控制码 ACK（手册 p.32）。</summary>
    public const byte Ack = 0x06;

    /// <summary>格式 4 异常应答的控制码 NAK（手册 p.32）。</summary>
    public const byte Nak = 0x15;

    /// <summary>格式 5 的数据链路转义字符 DLE（手册 p.33）。</summary>
    public const byte Dle = 0x10;

    /// <summary>帧识别号 F8（手册 p.45）。</summary>
    public const byte FrameId = 0xF8;

    /// <summary>格式 5 应答的响应 ID FF FF（手册 p.35），按小端读取。</summary>
    public const ushort ResponseId = 0xFFFF;
}
