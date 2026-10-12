namespace Junevy.Communication.Secs;

/// <summary>
/// SECS-II 数据项的格式（SEMI E5 第 9 节、P51-D3）。
/// </summary>
/// <remarks>
/// 枚举值就是线上格式字节的高 6 位（低 2 位留给长度字节数），因此<strong>线上格式字节 = 枚举值 | 长度字节数</strong>：
/// <c>A</c> 是 0x41（1 个长度字节）、0x42（2 个）、0x43（3 个）；<c>J</c> 是 0x45，<c>C2</c> 是 0x49。
/// 编码与解码因此都是位运算而不是查表。
/// 本库不支持 <c>J</c>（JIS-8）与 <c>C2</c>（2 字节字符），解码遇到它们按非法数据处理。
/// </remarks>
public enum SecsFormat
{
    /// <summary><c>L</c>：列表，长度是子项个数。</summary>
    List = 0x00,

    /// <summary><c>B</c>：二进制，长度是字节数。</summary>
    Binary = 0x20,

    /// <summary><c>BOOLEAN</c>：布尔值，长度是字节数，线上只写 0x00 与 0x01。</summary>
    Boolean = 0x24,

    /// <summary><c>A</c>：单字节字符（Latin-1），长度是字符数（P51-D4）。</summary>
    Ascii = 0x40,

    /// <summary><c>I8</c>：64 位有符号整数。</summary>
    I8 = 0x60,

    /// <summary><c>I1</c>：8 位有符号整数。</summary>
    I1 = 0x64,

    /// <summary><c>I2</c>：16 位有符号整数。</summary>
    I2 = 0x68,

    /// <summary><c>I4</c>：32 位有符号整数。</summary>
    I4 = 0x70,

    /// <summary><c>F8</c>：双精度浮点数。</summary>
    F8 = 0x80,

    /// <summary><c>F4</c>：单精度浮点数。</summary>
    F4 = 0x90,

    /// <summary><c>U8</c>：64 位无符号整数。</summary>
    U8 = 0xA0,

    /// <summary><c>U1</c>：8 位无符号整数。</summary>
    U1 = 0xA4,

    /// <summary><c>U2</c>：16 位无符号整数。</summary>
    U2 = 0xA8,

    /// <summary><c>U4</c>：32 位无符号整数。</summary>
    U4 = 0xB0,
}