namespace Junevy.Communication.Melsec;

/// <summary>MELSEC 的帧类型：以太网 3E / 4E（4C 串口帧在后续任务中加入）。</summary>
internal enum McFrameType
{
    /// <summary>3E 帧：副头部 50 00 / D0 00，无序列号。</summary>
    Frame3E,

    /// <summary>4E 帧：副头部 54 00 / D4 00，副头部后带 2 字节序列号与固定的 0000。</summary>
    Frame4E,
}
