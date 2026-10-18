using Junevy.Communication.Plc;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的软元件（存储区）定义：区、区域字母与区域代码。四个区都按字节寻址，编号一律十进制；
/// 编号上限是 S7 三字节的位地址所能表示的最大字节号（P4-D14、P4-D16）。实例由内部的软元件表创建，每个区一个。
/// </summary>
/// <remarks>
/// <see cref="Symbol"/>、<see cref="Radix"/> 与 <see cref="MaxNumber"/> 描述的是声明式地址解析器的输入格式。
/// S7 的地址由专门的解析器解析（数据块地址带两个数字，声明式解析器表达不了），这些成员只用于说明本区的写法与校验上限。
/// </remarks>
public sealed class S7Device : IPlcDeviceDefinition
{
    // 地址字段是 3 字节的位地址（17.3 节），换算成字节号即 0xFFFFFF / 8。
    private const int ThreeByteBitAddress = 0xFFFFFF;
    private const int BitsPerByte = 8;

    internal S7Device(S7Area area, string symbol, byte areaCode)
    {
        Area = area;
        Symbol = symbol;
        AreaCode = areaCode;
    }

    /// <summary>存储区。</summary>
    public S7Area Area { get; }

    /// <summary>区域字母："DB"、"M"、"I" 或 "Q"。</summary>
    public string Symbol { get; }

    /// <summary>区域代码：I 0x81、Q 0x82、M 0x83、DB 0x84（17.3 节）。</summary>
    public byte AreaCode { get; }

    /// <summary>单位：字节。S7 的读写长度由调用的方法决定，与宽度字母无关。</summary>
    public PlcAddressUnit Unit => PlcAddressUnit.Byte;

    /// <summary>编号进制：十进制（P4-D16）。</summary>
    public int Radix => 10;

    /// <summary>编号上限（含）：字节号 0–2097151。</summary>
    public int MaxNumber => ThreeByteBitAddress / BitsPerByte;
}
