using System.Text.Json.Serialization;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;
using Junevy.Communication.DebugAssistant.Protocols.Melsec;
using Junevy.Communication.DebugAssistant.Protocols.Modbus;

namespace Junevy.Communication.DebugAssistant.Workspace;

/// <summary>
/// 页签的参数（计划 DA-D11）。每种协议有一个派生的 record，并在这里加一行 <see cref="JsonDerivedTypeAttribute"/>，
/// 以区分种类。密码等敏感字段标记 <see cref="JsonIgnoreAttribute"/>，不写入文件。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(PlaceholderSettings), "placeholder")]
[JsonDerivedType(typeof(TcpClientSettings), "tcpClient")]
[JsonDerivedType(typeof(UdpSettings), "udp")]
[JsonDerivedType(typeof(SerialSettings), "serial")]
[JsonDerivedType(typeof(ModbusSettings), "modbus")]
[JsonDerivedType(typeof(McSettings), "mc")]
[JsonDerivedType(typeof(TcpServerSettings), "tcpServer")]
public abstract record SessionSettings;
