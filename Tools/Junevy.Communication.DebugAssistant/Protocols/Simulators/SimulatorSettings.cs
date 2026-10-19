using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Melsec;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Protocols.Simulators;

/// <summary>
/// 模拟设备页签的参数（计划 DA-D11）：宿主（传输、地址、端口、串口）与设备（站号、MC 的帧与编码、S7 的 PDU 与 PUT/GET、
/// FINS 的节点）。内存的数值与故障注入不保存：它们是运行时的状态。
/// </summary>
public sealed record SimulatorSettings(
    SimulatorTransport Transport,
    string Host,
    int Port,
    string PortName,
    int BaudRate,
    string UnitIds,
    bool ModbusRtu,
    McFrameKind McFrame,
    McEncodingKind McEncoding,
    bool McSumCheck,
    int S7Pdu,
    bool S7PutGet,
    int FinsServerNode,
    int FinsClientNode)
    : SessionSettings;
