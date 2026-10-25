namespace Junevy.Communication.DebugAssistant.Shell;

/// <summary>全部页签种类的登记表（DA-D19：新增种类只在这里加一条）。</summary>
public static class ProtocolCatalog
{
    private static readonly ProtocolDescriptor[] Descriptors =
    [
        new(ProtocolKind.Modbus, ProtocolGroup.Plc, "Modbus（TCP / RTU / UDP）", "Modbus", ""),
        new(ProtocolKind.Mc, ProtocolGroup.Plc, "三菱 MC（MELSEC）", "MC", ""),
        new(ProtocolKind.S7, ProtocolGroup.Plc, "西门子 S7", "S7", ""),
        new(ProtocolKind.Fins, ProtocolGroup.Plc, "欧姆龙 FINS", "FINS", ""),
        new(ProtocolKind.Secs, ProtocolGroup.Factory, "SECS/HSMS 设备端", "SECS", ""),
        new(ProtocolKind.Mqtt, ProtocolGroup.Factory, "MQTT 客户端", "MQTT", ""),
        new(ProtocolKind.OpcUa, ProtocolGroup.Factory, "OPC UA 客户端", "OPC UA", ""),
        new(ProtocolKind.RestClient, ProtocolGroup.Factory, "WebApi REST 客户端", "REST", ""),
        new(ProtocolKind.SoapClient, ProtocolGroup.Factory, "WebApi SOAP 客户端", "SOAP", ""),
        new(ProtocolKind.HttpServer, ProtocolGroup.Factory, "WebApi 服务端", "HTTP 服务端", ""),
        new(ProtocolKind.Ftp, ProtocolGroup.Factory, "FTP / FTPS 客户端", "FTP", ""),
        new(ProtocolKind.TcpClient, ProtocolGroup.Custom, "TCP 客户端", "TCP 客户端", ""),
        new(ProtocolKind.TcpServer, ProtocolGroup.Custom, "TCP 服务端", "TCP 服务端", ""),
        new(ProtocolKind.Udp, ProtocolGroup.Custom, "UDP", "UDP", ""),
        new(ProtocolKind.Serial, ProtocolGroup.Custom, "串口", "串口", ""),
        new(ProtocolKind.ModbusSlave, ProtocolGroup.Simulator, "Modbus 从站", "Modbus 从站", ""),
        new(ProtocolKind.McSlave, ProtocolGroup.Simulator, "MC 从站", "MC 从站", ""),
        new(ProtocolKind.S7Cpu, ProtocolGroup.Simulator, "S7 CPU", "S7 CPU", ""),
        new(ProtocolKind.FinsSlave, ProtocolGroup.Simulator, "FINS 从站", "FINS 从站", ""),
    ];

    private static readonly Dictionary<ProtocolKind, ProtocolDescriptor> ByKind = Descriptors.ToDictionary(d => d.Kind);

    /// <summary>按登记顺序排列的全部描述。</summary>
    public static IReadOnlyList<ProtocolDescriptor> All { get; } = Descriptors;

    /// <summary>取得某种页签的描述；未登记的种类抛出 <see cref="KeyNotFoundException"/>。</summary>
    public static ProtocolDescriptor Get(ProtocolKind kind) => ByKind[kind];
}
