using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 的软元件定义：符号与两个区域代码（字区、位区），对照计划第 17.3 节的码表。
/// FINS 的存储区一律按字寻址，位访问是"同一个字的位号 0–15 + 位区代码"，因此单位恒为字、编号进制恒为十进制（P4-D16）。
/// 实例由内部的软元件表创建。
/// </summary>
public sealed class FinsDevice : IPlcDeviceDefinition
{
    // 字号字段为 2 字节（17.2 节的内存区读命令）；实际可用范围因 CPU 型号而异，越界由结束码返回（P4-D16）。
    private const int TwoByteMaxNumber = 0xFFFF;

    // 编号与位号都用十进制写法（P4-D16）。
    private const int DecimalRadix = 10;

    internal FinsDevice(string symbol, byte wordAreaCode, byte bitAreaCode)
    {
        Symbol = symbol;
        WordAreaCode = wordAreaCode;
        BitAreaCode = bitAreaCode;
    }

    /// <summary>软元件符号，例如 "D"、"CIO"、"E0_"。</summary>
    public string Symbol { get; }

    /// <summary>单位：字软元件。FINS 没有位软元件，也没有字节软元件。</summary>
    public PlcAddressUnit Unit => PlcAddressUnit.Word;

    /// <summary>编号进制：十进制（P4-D16）。</summary>
    public int Radix => DecimalRadix;

    /// <summary>编号上限（含）：2 字节，0xFFFF。</summary>
    public int MaxNumber => TwoByteMaxNumber;

    /// <summary>字区代码，例如 CIO 区 0xB0（第 17.3 节）。</summary>
    public byte WordAreaCode { get; }

    /// <summary>位区代码，例如 CIO 区 0x30；位访问用它替换字区代码（第 17.3 节）。</summary>
    public byte BitAreaCode { get; }
}
