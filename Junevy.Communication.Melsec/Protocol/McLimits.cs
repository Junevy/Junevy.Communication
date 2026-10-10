namespace Junevy.Communication.Melsec.Protocol;

/// <summary>以太网帧的点数上限（Q/L 兼容；手册 p.70 的点数范围表）。</summary>
internal static class McLimits
{
    /// <summary>字访问的最大字数（所有传输与编码）。</summary>
    public const int WordPoints = 960;

    /// <summary>以太网二进制的位访问最大点数（E71 二进制）。</summary>
    public const int EthernetBinaryBitPoints = 7168;

    /// <summary>以太网 ASCII 的位访问最大点数（E71 ASCII）。</summary>
    public const int EthernetAsciiBitPoints = 3584;

    /// <summary>串口（C24）的位访问最大点数，二进制与 ASCII 相同（手册 p.70）。</summary>
    public const int SerialBitPoints = 7904;
}
