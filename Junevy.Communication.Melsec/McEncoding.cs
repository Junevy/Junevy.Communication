namespace Junevy.Communication.Melsec;

/// <summary>
/// MC 协议的数据编码：二进制，或 ASCII（数值写为大写十六进制文本，高位在前）。
/// 4C 帧中，二进制对应格式 5，ASCII 对应格式 4。
/// </summary>
internal enum McEncoding
{
    /// <summary>二进制：多字节数值小端，软元件编号 3 字节。</summary>
    Binary,

    /// <summary>ASCII：数值为十六进制文本，软元件编号为 6 位数字。</summary>
    Ascii,
}
