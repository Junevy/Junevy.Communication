using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 的存储区定义：线圈、离散输入、输入寄存器与保持寄存器。符号、单位、只读属性与功能码由这里决定（P7-D4、P7-D5）。
/// 四个存储区都是静态实例；地址解析与功能码映射都从这里取值。
/// </summary>
public sealed class ModbusDevice : IPlcDeviceDefinition
{
    private const int DecimalRadix = 10;
    private const int MaxAddressNumber = 65535;

    /// <summary>线圈：位，读写。符号 <c>C</c>，Modicon 首位为 0。</summary>
    public static readonly ModbusDevice Coil = new ModbusDevice(
        "C", PlcAddressUnit.Bit, isReadOnly: false, modiconArea: '0', readFunctionCode: 0x01, writeSingleFunctionCode: 0x05, writeMultipleFunctionCode: 0x0F);

    /// <summary>离散输入：位，只读。符号 <c>DI</c>，Modicon 首位为 1。</summary>
    public static readonly ModbusDevice DiscreteInput = new ModbusDevice(
        "DI", PlcAddressUnit.Bit, isReadOnly: true, modiconArea: '1', readFunctionCode: 0x02, writeSingleFunctionCode: null, writeMultipleFunctionCode: null);

    /// <summary>输入寄存器：字，只读。符号 <c>IR</c>，Modicon 首位为 3。</summary>
    public static readonly ModbusDevice InputRegister = new ModbusDevice(
        "IR", PlcAddressUnit.Word, isReadOnly: true, modiconArea: '3', readFunctionCode: 0x04, writeSingleFunctionCode: null, writeMultipleFunctionCode: null);

    /// <summary>保持寄存器：字，读写。符号 <c>HR</c>，Modicon 首位为 4。</summary>
    public static readonly ModbusDevice HoldingRegister = new ModbusDevice(
        "HR", PlcAddressUnit.Word, isReadOnly: false, modiconArea: '4', readFunctionCode: 0x03, writeSingleFunctionCode: 0x06, writeMultipleFunctionCode: 0x10);

    internal ModbusDevice(string symbol, PlcAddressUnit unit, bool isReadOnly, char modiconArea, byte readFunctionCode,
                          byte? writeSingleFunctionCode, byte? writeMultipleFunctionCode)
    {
        Symbol = symbol;
        Unit = unit;
        IsReadOnly = isReadOnly;
        ModiconArea = modiconArea;
        ReadFunctionCode = readFunctionCode;
        WriteSingleFunctionCode = writeSingleFunctionCode;
        WriteMultipleFunctionCode = writeMultipleFunctionCode;
    }

    /// <summary>软元件符号（大小写不敏感）：<c>C</c>、<c>DI</c>、<c>IR</c>、<c>HR</c>。</summary>
    public string Symbol { get; }

    /// <summary>访问单位：线圈与离散输入是位，寄存器是字。</summary>
    public PlcAddressUnit Unit { get; }

    /// <summary>编号的进制，固定为 10。</summary>
    public int Radix => DecimalRadix;

    /// <summary>编号的上限，固定为 65535（协议地址 0–65535）。</summary>
    public int MaxNumber => MaxAddressNumber;

    /// <summary>是否只读。只读存储区拒绝一切写入（P7-S1）。</summary>
    public bool IsReadOnly { get; }

    /// <summary>Modicon 写法的首位数字（0、1、3、4）。</summary>
    internal char ModiconArea { get; }

    /// <summary>读取功能码（01、02、03、04）。</summary>
    internal byte ReadFunctionCode { get; }

    /// <summary>写单个的功能码（05、06）；只读存储区为 null。</summary>
    internal byte? WriteSingleFunctionCode { get; }

    /// <summary>写多个的功能码（0F、10）；只读存储区为 null。</summary>
    internal byte? WriteMultipleFunctionCode { get; }
}
