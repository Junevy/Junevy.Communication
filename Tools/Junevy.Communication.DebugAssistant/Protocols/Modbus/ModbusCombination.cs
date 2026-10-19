using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.Modbus;

namespace Junevy.Communication.DebugAssistant.Protocols.Modbus;

/// <summary>Modbus 页签的帧格式选择：自动（串口为 RTU，网络为 MBAP），或明确指定。</summary>
public enum ModbusFrameChoice
{
    Auto,
    Mbap,
    Rtu,
}

/// <summary>传输与帧格式的组合（计划第 7.2 节）：界面显示组合的名称；"串口 + MBAP"不可选，并说明原因。</summary>
public static class ModbusCombination
{
    /// <summary>选择对应的库帧格式；自动返回 null（由库按传输决定）。</summary>
    public static ModbusFraming? ToFraming(ModbusFrameChoice choice) => choice switch
    {
        ModbusFrameChoice.Mbap => ModbusFraming.Mbap,
        ModbusFrameChoice.Rtu => ModbusFraming.Rtu,
        _ => null,
    };

    /// <summary>自动解析后的帧格式：串口为 RTU，网络为 MBAP。</summary>
    public static ModbusFraming Resolve(PlcTransport transport, ModbusFrameChoice choice) =>
        ToFraming(choice) ?? (transport == PlcTransport.Serial ? ModbusFraming.Rtu : ModbusFraming.Mbap);

    /// <summary>组合不合法时的原因；合法时返回 null。</summary>
    public static string? Problem(PlcTransport transport, ModbusFrameChoice choice) =>
        transport == PlcTransport.Serial && choice == ModbusFrameChoice.Mbap
            ? "串口只能使用 RTU 帧（Modbus 串行链路没有 MBAP），请选择 RTU 或自动。"
            : null;

    /// <summary>组合的名称（Modbus TCP、RTU over TCP、Modbus UDP、RTU over UDP、Modbus RTU）；不合法时返回 null。</summary>
    public static string? Name(PlcTransport transport, ModbusFrameChoice choice)
    {
        if (Problem(transport, choice) is not null)
        {
            return null;
        }

        return (transport, Resolve(transport, choice)) switch
        {
            (PlcTransport.Tcp, ModbusFraming.Mbap) => "Modbus TCP",
            (PlcTransport.Tcp, ModbusFraming.Rtu) => "RTU over TCP",
            (PlcTransport.Udp, ModbusFraming.Mbap) => "Modbus UDP",
            (PlcTransport.Udp, ModbusFraming.Rtu) => "RTU over UDP",
            (PlcTransport.Serial, ModbusFraming.Rtu) => "Modbus RTU",
            _ => null,
        };
    }
}
