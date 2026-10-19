namespace Junevy.Communication.DebugAssistant.Shell;

/// <summary>调试页签的种类（计划第 4.3 节，共 19 种）。新增种类的步骤见计划 DA-D19。</summary>
public enum ProtocolKind
{
    Modbus,
    Mc,
    S7,
    Fins,
    Secs,
    Mqtt,
    OpcUa,
    RestClient,
    SoapClient,
    HttpServer,
    Ftp,
    TcpClient,
    TcpServer,
    Udp,
    Serial,
    ModbusSlave,
    McSlave,
    S7Cpu,
    FinsSlave,
}
