namespace Junevy.Communication.Core.Buffers;

/// <summary>
/// 字序：多字节数值在设备字节流中的排列。数值的大端字节 B0…Bn-1（B0 为最高位）被切成 16 位字 W0…Wk-1（W0 为最高字）。
/// 名称描述传输顺序：<c>ABCD</c> 为大端，<c>DCBA</c> 为小端；16 位数值中 <c>ABCD</c> 与 <c>CDAB</c> 为大端，<c>BADC</c> 与 <c>DCBA</c> 为小端。
/// </summary>
public enum WordOrder
{
    /// <summary>字顺序正常（W0 在前），字内高字节在前：B0 B1 B2 B3 …</summary>
    ABCD,

    /// <summary>字顺序正常（W0 在前），字内低字节在前：B1 B0 B3 B2 …</summary>
    BADC,

    /// <summary>字顺序颠倒（Wk-1 在前），字内高字节在前：B(n-2) B(n-1) … B0 B1</summary>
    CDAB,

    /// <summary>字顺序颠倒（Wk-1 在前），字内低字节在前（小端）：B(n-1) B(n-2) … B0</summary>
    DCBA,
}
