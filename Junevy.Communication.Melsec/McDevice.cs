using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC Q/L 兼容的软元件定义：符号、单位、编号进制，以及 ASCII 代码（2 个字符）与二进制代码（1 字节）。
/// 编号上限固定为 3 字节（手册 p.67）。实例由内部的软元件表创建。
/// </summary>
public sealed class McDevice : IPlcDeviceDefinition
{
    // Q/L 兼容的软元件编号为 3 字节（手册 p.67）。
    private const int ThreeByteMaxNumber = 0xFFFFFF;

    internal McDevice(string symbol, PlcAddressUnit unit, int radix, string asciiCode, byte binaryCode)
    {
        Symbol = symbol;
        Unit = unit;
        Radix = radix;
        AsciiCode = asciiCode;
        BinaryCode = binaryCode;
    }

    /// <summary>软元件符号，例如 "D"、"ZR"、"SM"。</summary>
    public string Symbol { get; }

    /// <summary>单位：位软元件或字软元件。</summary>
    public PlcAddressUnit Unit { get; }

    /// <summary>编号进制：10 或 16（手册 p.67 的 Notation 列）。</summary>
    public int Radix { get; }

    /// <summary>编号上限（含）：3 字节，0xFFFFFF。</summary>
    public int MaxNumber => ThreeByteMaxNumber;

    /// <summary>ASCII 代码：2 个字符，例如 "D*"、"ZR"、"SM"（手册 p.67–68）。</summary>
    public string AsciiCode { get; }

    /// <summary>二进制代码：1 字节，例如 0xA8（手册 p.67–68）。</summary>
    public byte BinaryCode { get; }
}
