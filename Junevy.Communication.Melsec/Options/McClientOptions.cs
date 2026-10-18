using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 客户端选项（P2-D9、P2-D15）。客户端构造时校验并取快照；之后修改选项对象不影响已创建的客户端，也不改写选项本身。
/// </summary>
public sealed class McClientOptions : PlcClientOptions
{
    /// <summary>帧类型：3E、4E 用于以太网（TCP、UDP），4C 用于串口。默认 3E。</summary>
    public McFrameType FrameType { get; set; } = McFrameType.Frame3E;

    /// <summary>数据编码：二进制或 ASCII；4C 帧中二进制为格式 5，ASCII 为格式 4。默认二进制。</summary>
    public McEncoding Encoding { get; set; } = McEncoding.Binary;

    /// <summary>访问路径：网络号。默认 00H。</summary>
    public byte NetworkNumber { get; set; } = 0x00;

    /// <summary>访问路径：PC 号。默认 FFH。</summary>
    public byte PcNumber { get; set; } = 0xFF;

    /// <summary>访问路径：请求目标模块 I/O 号。默认 03FFH。</summary>
    public ushort ModuleIoNumber { get; set; } = 0x03FF;

    /// <summary>访问路径：请求目标模块站号。默认 00H。</summary>
    public byte ModuleStationNumber { get; set; } = 0x00;

    /// <summary>监视定时器，单位 250 ms，取值 1 至 65535（P2-D15）。默认 0004H（1 s）。</summary>
    public ushort MonitoringTimer { get; set; } = 0x0004;

    /// <summary>访问路径：站号（4C）。默认 00H。</summary>
    public byte StationNumber { get; set; } = 0x00;

    /// <summary>访问路径：本站号（4C）。默认 00H。</summary>
    public byte SelfStationNumber { get; set; } = 0x00;

    /// <summary>是否使用和校验（4C），必须与 PLC 侧设置一致。默认开启。</summary>
    public bool SumCheck { get; set; } = true;

    /// <summary>字序与字符串编码；为 null 时使用 DCBA 字序与 ASCII 字符串（P2-D9）。</summary>
    public PlcDataFormat? DataFormat { get; set; }
}
