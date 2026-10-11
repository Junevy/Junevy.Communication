namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的软元件表：四个区各一个实例，区域代码对照计划的码表（17.3 节）。
/// </summary>
internal static class S7DeviceTable
{
    /// <summary>过程输入区 I。</summary>
    public static readonly S7Device Inputs = new(S7Area.Inputs, "I", 0x81);

    /// <summary>过程输出区 Q。</summary>
    public static readonly S7Device Outputs = new(S7Area.Outputs, "Q", 0x82);

    /// <summary>位存储区 M。</summary>
    public static readonly S7Device Merkers = new(S7Area.Merkers, "M", 0x83);

    /// <summary>数据块 DB。</summary>
    public static readonly S7Device DataBlock = new(S7Area.DataBlock, "DB", 0x84);
}
