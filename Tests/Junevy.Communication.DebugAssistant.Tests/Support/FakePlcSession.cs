using Junevy.Communication.Core.Buffers;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>测试用的 PLC 页签：客户端是 <see cref="FakePlcClient"/>，公开共同部分的参数保存入口。</summary>
internal sealed class FakePlcSession : PlcSessionViewModel
{
    public FakePlcSession(SessionServices services, FakePlcClient client, ChannelSettingsEditor? channel = null)
        : base(ProtocolCatalog.Get(ProtocolKind.Modbus), services, channel ?? new ChannelSettingsEditor(ChannelTransport.Tcp, protocolOwnsChannelFraming: true), WordOrder.ABCD)
    {
        Client = client;
    }

    public FakePlcClient Client { get; }

    protected override IPlcClient CreateClient() => Client;

    public override SessionSettings CaptureSettings() => new PlaceholderSettings();

    public override void ApplySettings(SessionSettings settings)
    {
    }

    public PlcPanelSettings CapturePanelForTest() => CapturePanel();

    public void ApplyPanelForTest(PlcPanelSettings saved) => ApplyPanel(saved);
}
