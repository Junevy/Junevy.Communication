namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>
/// PLC 面板的数据类型（计划第 7.1 节）。<see cref="Count"/> 的含义随类型不同：数组的元素个数、字符串的字节长度、
/// 字节类型的字节数，或原始字的字数。
/// </summary>
public enum PlcValueType
{
    /// <summary>16 位有符号整数。</summary>
    Int16,

    /// <summary>16 位无符号整数。</summary>
    UInt16,

    /// <summary>32 位有符号整数（占两个字）。</summary>
    Int32,

    /// <summary>32 位无符号整数（占两个字）。</summary>
    UInt32,

    /// <summary>64 位有符号整数（占四个字）。</summary>
    Int64,

    /// <summary>64 位无符号整数（占四个字）。</summary>
    UInt64,

    /// <summary>单精度浮点数（占两个字）。</summary>
    Float,

    /// <summary>双精度浮点数（占四个字）。</summary>
    Double,

    /// <summary>字符串；<see cref="Count"/> 为字节长度，写入时以它作为定长。</summary>
    String,

    /// <summary>单个位。</summary>
    Bool,

    /// <summary>若干个位。</summary>
    Bits,

    /// <summary>单个字节。</summary>
    Byte,

    /// <summary>若干个字节。</summary>
    Bytes,

    /// <summary>原始字（协议的规范化布局，<see cref="Count"/> 为字数）。</summary>
    Words,
}
