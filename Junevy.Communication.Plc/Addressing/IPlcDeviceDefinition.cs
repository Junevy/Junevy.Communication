namespace Junevy.Communication.Plc;

/// <summary>
/// 软元件（存储区）的定义：符号、单位、编号进制与编号上限。协议用一张声明式的软元件表提供它的实例，地址解析器据此解析文本。
/// </summary>
public interface IPlcDeviceDefinition
{
    /// <summary>软元件符号，例如 "D"、"ZR"、"SM"；解析时大小写不敏感。</summary>
    string Symbol { get; }

    /// <summary>软元件的单位：位、字还是字节。</summary>
    PlcAddressUnit Unit { get; }

    /// <summary>编号的进制：10 或 16。</summary>
    int Radix { get; }

    /// <summary>编号的上限（含）。</summary>
    int MaxNumber { get; }
}
