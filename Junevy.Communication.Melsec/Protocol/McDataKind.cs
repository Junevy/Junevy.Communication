namespace Junevy.Communication.Melsec.Protocol;

/// <summary>应答数据的种类：决定解码时的长度校验与规范化结果。</summary>
internal enum McDataKind
{
    /// <summary>无数据（写入的应答）。</summary>
    None,

    /// <summary>字单位数据，规范化为每字 2 字节小端。</summary>
    Words,

    /// <summary>位单位数据，规范化为每点 1 个字节（0 或 1）。</summary>
    Bits,

    /// <summary>回环数据（0619 的回显），规范化为原始字节。</summary>
    Loopback,
}
