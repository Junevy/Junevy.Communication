namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的存储区：过程输入 I、过程输出 Q、位存储区 M 与数据块 DB。区域代码见计划的码表（17.3 节）。
/// </summary>
public enum S7Area
{
    /// <summary>过程输入区 I，区域代码 0x81。</summary>
    Inputs,

    /// <summary>过程输出区 Q，区域代码 0x82。</summary>
    Outputs,

    /// <summary>位存储区 M（内部标志位），区域代码 0x83。</summary>
    Merkers,

    /// <summary>数据块 DB，区域代码 0x84。</summary>
    DataBlock,
}
