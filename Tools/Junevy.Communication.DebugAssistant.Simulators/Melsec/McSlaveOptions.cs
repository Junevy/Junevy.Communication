namespace Junevy.Communication.DebugAssistant.Simulators.Melsec;

/// <summary>MC 的帧类型：3E、4E（TCP 与 UDP），以及串口上的 4C。</summary>
public enum McFrameKind
{
    Frame3E,
    Frame4E,
    Frame4C,
}

/// <summary>MC 的编码：二进制，或 ASCII。4C 中二进制为格式 5，ASCII 为格式 4。</summary>
public enum McEncodingKind
{
    Binary,
    Ascii,
}

/// <summary>MC 从站的设置（计划第 7.14 节）：帧类型、编码，以及 4C 的和校验是否开启（只影响应答）。</summary>
public sealed record McSlaveOptions(McFrameKind Frame, McEncodingKind Encoding, bool SumCheck = true);
