using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Siemens;

namespace Junevy.Communication.DebugAssistant.Protocols.Siemens;

/// <summary>S7 页签的参数（计划 DA-D11）：连接地址、机架、槽号与连接类型，字符串面板的参数，以及公共面板的参数。</summary>
public sealed record S7Settings(
    PlcPanelSettings Panel,
    string Host,
    int Port,
    int Rack,
    int Slot,
    S7ConnectionType ConnectionType,
    S7StringKind StringKind,
    int StringMaxLength,
    string StringAddress,
    string StringValue)
    : SessionSettings;
