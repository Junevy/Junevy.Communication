namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 的帧类型：以太网 3E、4E（TCP 与 UDP），以及串口 4C（格式 4 或格式 5，由 <see cref="McEncoding"/> 决定）。
/// </summary>
public enum McFrameType
{
    /// <summary>3E 帧：副头部 50 00（二进制）或 5000（ASCII），无序列号。</summary>
    Frame3E,

    /// <summary>4E 帧：副头部之后带 2 字节序列号与固定的 0000，应答按序列号匹配。</summary>
    Frame4E,

    /// <summary>串口 4C 帧：二进制为格式 5（DLE 边界与和校验），ASCII 为格式 4（ENQ、STX、ACK、NAK）。</summary>
    Frame4C,
}
