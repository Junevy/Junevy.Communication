using Junevy.Communication.DebugAssistant.Shell;

namespace Junevy.Communication.DebugAssistant.Tests.Shell;

public sealed class ProtocolCatalogTests
{
    [Fact]
    public void Catalog_ListsEveryKindExactlyOnce()
    {
        ProtocolKind[] kinds = Enum.GetValues<ProtocolKind>();

        Assert.Equal(19, kinds.Length);
        Assert.Equal(kinds.OrderBy(k => k), ProtocolCatalog.All.Select(d => d.Kind).OrderBy(k => k));
    }

    [Theory]
    [InlineData(ProtocolKind.Modbus, ProtocolGroup.Plc, "Modbus")]
    [InlineData(ProtocolKind.Mc, ProtocolGroup.Plc, "MC")]
    [InlineData(ProtocolKind.S7, ProtocolGroup.Plc, "S7")]
    [InlineData(ProtocolKind.Fins, ProtocolGroup.Plc, "FINS")]
    [InlineData(ProtocolKind.Secs, ProtocolGroup.Factory, "SECS")]
    [InlineData(ProtocolKind.Mqtt, ProtocolGroup.Factory, "MQTT")]
    [InlineData(ProtocolKind.OpcUa, ProtocolGroup.Factory, "OPC UA")]
    [InlineData(ProtocolKind.RestClient, ProtocolGroup.Factory, "REST")]
    [InlineData(ProtocolKind.SoapClient, ProtocolGroup.Factory, "SOAP")]
    [InlineData(ProtocolKind.HttpServer, ProtocolGroup.Factory, "HTTP 服务端")]
    [InlineData(ProtocolKind.Ftp, ProtocolGroup.Factory, "FTP")]
    [InlineData(ProtocolKind.TcpClient, ProtocolGroup.Custom, "TCP 客户端")]
    [InlineData(ProtocolKind.TcpServer, ProtocolGroup.Custom, "TCP 服务端")]
    [InlineData(ProtocolKind.Udp, ProtocolGroup.Custom, "UDP")]
    [InlineData(ProtocolKind.Serial, ProtocolGroup.Custom, "串口")]
    [InlineData(ProtocolKind.ModbusSlave, ProtocolGroup.Simulator, "Modbus 从站")]
    [InlineData(ProtocolKind.McSlave, ProtocolGroup.Simulator, "MC 从站")]
    [InlineData(ProtocolKind.S7Cpu, ProtocolGroup.Simulator, "S7 CPU")]
    [InlineData(ProtocolKind.FinsSlave, ProtocolGroup.Simulator, "FINS 从站")]
    public void Descriptor_HasTheGroupAndDefaultNameOfTheDesign(ProtocolKind kind, ProtocolGroup group, string shortName)
    {
        ProtocolDescriptor descriptor = ProtocolCatalog.Get(kind);

        Assert.Equal(group, descriptor.Group);
        Assert.Equal(shortName, descriptor.ShortName);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Title));
        Assert.Single(descriptor.Glyph);
    }
}
