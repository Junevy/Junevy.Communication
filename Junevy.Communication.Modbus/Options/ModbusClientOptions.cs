using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 客户端的选项（P7-D3、D5、D7、D11）。重试与读-改-写选项继承自套件；Modbus 专有的选项在这里。
/// 创建客户端时取快照，之后修改对象不影响客户端。
/// </summary>
public sealed class ModbusClientOptions : PlcClientOptions
{
    /// <summary>帧格式。为 null 时按介质决定：串口为 RTU，网络（TCP、UDP）为 MBAP。</summary>
    public ModbusFraming? Framing { get; set; }

    /// <summary>默认站号：地址没有 <c>s=n;</c> 前缀时使用。RTU 为 1–247，MBAP 为 0–255。</summary>
    public byte UnitId { get; set; } = 1;

    /// <summary>
    /// 写入一个点或一个字时是否使用 05、06。为 false 时统一使用 0F、10（部分设备只接受多写功能码，P7-D17 ④）。
    /// </summary>
    public bool UseSingleWriteFunctions { get; set; } = true;

    /// <summary>寄存器中的位写入是否使用掩码写（0x16，原子执行）。为 false 时位写入返回 <c>NotSupported</c>，除非开启读-改-写。</summary>
    public bool UseMaskWriteForBits { get; set; }

    /// <summary>字序与字符串编码。为 null 时使用 ABCD 字序与 ASCII 字符串（P7-D11）。</summary>
    public PlcDataFormat? DataFormat { get; set; }
}
