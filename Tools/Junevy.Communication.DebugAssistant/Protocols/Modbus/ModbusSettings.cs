using System.Globalization;
using System.IO.Ports;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Protocols.Modbus;

/// <summary>Modbus 页签的参数（计划 DA-D11）：传输与帧格式、站号、串口参数、Modbus 选项，以及公共面板的参数。</summary>
public sealed record ModbusSettings(
    PlcPanelSettings Panel,
    PlcTransport Transport,
    ModbusFrameChoice Frame,
    string Host,
    int Port,
    byte UnitId,
    string PortName,
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    bool UseSingleWriteFunctions,
    bool UseMaskWriteForBits)
    : SessionSettings;

/// <summary>结果表格的一行（计划第 7.2 节）：地址、位值，或寄存器的无符号、有符号、十六进制与二进制显示。</summary>
public sealed record ModbusTableRow(string Address, string Bit, string Unsigned, string Signed, string Hex, string Binary)
{
    public static ModbusTableRow ForBit(string symbol, int number, bool value) =>
        new($"{symbol}{number}", value ? "1" : "0", string.Empty, string.Empty, string.Empty, string.Empty);

    public static ModbusTableRow ForWord(string symbol, int number, ushort value) => new(
        $"{symbol}{number}",
        string.Empty,
        value.ToString(CultureInfo.InvariantCulture),
        ((short)value).ToString(CultureInfo.InvariantCulture),
        $"0x{value:X4}",
        Convert.ToString(value, 2).PadLeft(16, '0'));
}
