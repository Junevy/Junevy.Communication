using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Protocols.Fins;

/// <summary>FINS 页签的参数（计划 DA-D11）：连接地址、客户端节点与目的网络、单元，以及公共面板的参数。</summary>
public sealed record FinsSettings(
    PlcPanelSettings Panel,
    string Host,
    int Port,
    int ClientNode,
    int DestinationNetwork,
    int DestinationUnit)
    : SessionSettings;
