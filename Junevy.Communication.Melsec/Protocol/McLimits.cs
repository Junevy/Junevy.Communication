namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 点数上限（Q/L 兼容）。取值来自各命令节内的点数范围表：字访问见批量读 p.87、批量写 p.93；位访问见批量读 p.91、批量写 p.96。
/// 由 McClient 的 GetMax*Points 与 ValidateRange 使用，按传输与编码选取。
/// </summary>
internal static class McLimits
{
    /// <summary>字访问的最大字数（所有传输与编码）。</summary>
    public const int WordPoints = 960;

    /// <summary>以太网二进制（E71 二进制）的位访问最大点数。</summary>
    public const int EthernetBinaryBitPoints = 7168;

    /// <summary>以太网 ASCII（E71 ASCII）的位访问最大点数。</summary>
    public const int EthernetAsciiBitPoints = 3584;

    /// <summary>串口（C24）的位访问最大点数，二进制与 ASCII 相同。</summary>
    public const int SerialBitPoints = 7904;

    /// <summary>一个字包含的位数；字访问位软元件时，每个字覆盖 16 个点。</summary>
    public const int BitsPerWord = 16;
}
