using System.IO.Ports;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Melsec;

namespace Junevy.Communication.DebugAssistant.Protocols.Melsec;

/// <summary>
/// MC 页签的参数（计划 DA-D11）：传输、帧、编码与和校验，通道的网络与串口参数，访问路径（十六进制文字），监视定时器，以及公共面板的参数。
/// </summary>
public sealed record McSettings(
    PlcPanelSettings Panel,
    PlcTransport Transport,
    McFrameType Frame,
    McEncoding Encoding,
    bool SumCheck,
    string Host,
    int Port,
    string PortName,
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    string NetworkNumberHex,
    string PcNumberHex,
    string ModuleIoNumberHex,
    string ModuleStationNumberHex,
    string StationNumberHex,
    string SelfStationNumberHex,
    int MonitoringTimerUnits)
    : SessionSettings;
