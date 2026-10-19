using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 的地址：存储区、编号、位号，以及可选的站号前缀 <c>s=n;</c>（P7-D4）。
/// 实例由地址解析器创建；没有站号前缀时 <see cref="UnitId"/> 为 null，请求使用客户端选项中的站号。
/// </summary>
public sealed class ModbusAddress : PlcAddress
{
    internal ModbusAddress(ModbusDevice device, int number, int? bitIndex, byte? unitId, string text)
        : base(text, device, number, bitIndex)
    {
        UnitId = unitId;
    }

    /// <summary>存储区定义（类型收窄为 <see cref="ModbusDevice"/>）。</summary>
    public new ModbusDevice Device => (ModbusDevice)base.Device;

    /// <summary>站号前缀中的站号（0–255，按帧格式在客户端检查）；没有前缀时为 null。</summary>
    public byte? UnitId { get; }
}
