namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 3E / 4E 的访问路径（手册 p.42）：网络号 1 字节、PC 号 1 字节、请求目标模块 I/O 号 2 字节、请求目标模块站号 1 字节。
/// </summary>
internal readonly struct McAccessRoute
{
    /// <summary>创建访问路径。</summary>
    public McAccessRoute(byte networkNumber, byte pcNumber, ushort moduleIoNumber, byte moduleStationNumber)
    {
        NetworkNumber = networkNumber;
        PcNumber = pcNumber;
        ModuleIoNumber = moduleIoNumber;
        ModuleStationNumber = moduleStationNumber;
    }

    /// <summary>默认访问路径（计划 §18）：网络 00、PC FF、I/O 03FF、模块站 00。</summary>
    public static McAccessRoute DefaultEthernet { get; } = new(0x00, 0xFF, 0x03FF, 0x00);

    /// <summary>网络号。</summary>
    public byte NetworkNumber { get; }

    /// <summary>PC 号。</summary>
    public byte PcNumber { get; }

    /// <summary>请求目标模块 I/O 号。</summary>
    public ushort ModuleIoNumber { get; }

    /// <summary>请求目标模块站号。</summary>
    public byte ModuleStationNumber { get; }
}
