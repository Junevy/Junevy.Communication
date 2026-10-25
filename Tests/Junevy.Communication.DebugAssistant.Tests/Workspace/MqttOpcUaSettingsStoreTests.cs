using System.IO;
using Junevy.Communication.DebugAssistant.Protocols.Mqtt;
using Junevy.Communication.DebugAssistant.Protocols.OpcUa;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Mqtt;

namespace Junevy.Communication.DebugAssistant.Tests.Workspace;

/// <summary>
/// MQTT 与 OPC UA 页的参数经工作区文件往返（计划 DA-D11）：多态类型标识 "mqtt" 与 "opcUa" 能读回同一个记录，
/// 没有保存密码，读回的记录与保存的相等。
/// </summary>
public sealed class MqttOpcUaSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void MqttAndOpcUaSettings_RoundTripThroughTheWorkspaceFile()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "workspace.json");
        var mqtt = new MqttSettings(
            "broker.example",
            8883,
            "device-1",
            "operator",
            MqttProtocolVersion.V500,
            false,
            120,
            30,
            5000,
            true,
            false,
            true,
            500,
            4000,
            "devices/1/status",
            "offline",
            MqttQos.ExactlyOnce,
            true);
        var opcUa = new OpcUaSettings(
            "opc.tcp://plc.example:4840/UA",
            "Junevy.DebugAssistant",
            "urn:junevy:debug",
            @"C:\pki",
            3000,
            4000,
            90000,
            2000,
            500,
            8000,
            250,
            100,
            5,
            false);
        new WorkspaceStore(path).Save(new WorkspaceDocument(
            WorkspaceDocument.CurrentSchemaVersion,
            [new WorkspaceSession("Mqtt", "MQTT #1", mqtt), new WorkspaceSession("OpcUa", "OPC UA #1", opcUa)],
            SelectedIndex: 0));

        WorkspaceLoadResult loaded = new WorkspaceStore(path).Load();

        Assert.Null(loaded.Notice);
        Assert.Equal(mqtt, loaded.Document.Sessions[0].Settings);
        Assert.Equal(opcUa, loaded.Document.Sessions[1].Settings);
        Assert.Contains("\"type\": \"mqtt\"", File.ReadAllText(path));
        Assert.Contains("\"type\": \"opcUa\"", File.ReadAllText(path));
    }
}
