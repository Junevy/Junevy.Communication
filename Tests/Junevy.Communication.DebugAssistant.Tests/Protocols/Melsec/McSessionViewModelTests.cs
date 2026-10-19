using System.Text.Json;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Melsec;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Melsec;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Melsec;

/// <summary>
/// MC 页签（计划 Task 9 第 1、2 条）：帧列表随传输变化，不合法的组合说明原因且连接时拒绝，访问路径的十六进制输入随请求发出，
/// 监视定时器的换算与警告，软元件速查表填入地址，以及设置在工作区 JSON 中往返。
/// </summary>
public sealed class McSessionViewModelTests
{
    private static McSessionViewModel CreatePage(ManualUiDispatcher dispatcher, ScriptedTransports transports) =>
        new(ProtocolCatalog.Get(ProtocolKind.Mc), FakeServices.Create(dispatcher, transports: transports));

    [Fact]
    public async Task FrameList_OnlyOffersTheFramesOfTheSelectedTransport()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        Assert.Equal(new[] { McFrameType.Frame3E, McFrameType.Frame4E }, page.AvailableFrames);

        page.Transport = PlcTransport.Udp;
        Assert.Equal(new[] { McFrameType.Frame3E, McFrameType.Frame4E }, page.AvailableFrames);

        page.Transport = PlcTransport.Serial;
        Assert.Equal(new[] { McFrameType.Frame4C }, page.AvailableFrames);
        Assert.Equal((McFrameType.Frame4C, true, ChannelTransport.Serial), (page.Frame, page.IsSerial, page.Channel.Transport));
        Assert.Equal("4C 格式 5（二进制，串口）", page.CombinationName);

        page.Transport = PlcTransport.Tcp;
        Assert.Equal((McFrameType.Frame3E, false), (page.Frame, page.IsSerial));
    }

    [Theory]
    [InlineData(PlcTransport.Tcp, McFrameType.Frame4C)]
    [InlineData(PlcTransport.Udp, McFrameType.Frame4C)]
    [InlineData(PlcTransport.Serial, McFrameType.Frame3E)]
    [InlineData(PlcTransport.Serial, McFrameType.Frame4E)]
    public async Task AnIllegalCombination_IsExplained_AndRefusedOnConnect(PlcTransport transport, McFrameType frame)
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Transport = transport;
        page.Frame = frame;

        Assert.Equal(McCombination.Problem(transport, frame), page.CombinationName);

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations).ErrorKind);
        Assert.Equal(0, transports.Tcp.CreateCount + transports.Udp.CreateCount + transports.Serial.CreateCount);
    }

    [Theory]
    [InlineData(McFrameType.Frame3E, CorrelationMode.Sequential)]
    [InlineData(McFrameType.Frame4E, CorrelationMode.Keyed)]
    public async Task Connect_OverTcp_PassesTheHostAndTheCorrelationOfTheFrame(McFrameType frame, CorrelationMode correlation)
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Host = "10.1.2.3";
        page.Port = 5010;
        page.Frame = frame;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Connected, page.State);
        var config = Assert.IsType<TcpClientChannelConfig>(transports.Tcp.Config);
        Assert.Equal(("10.1.2.3", 5010), (config.Host, config.Port));
        Assert.Equal(correlation, transports.Tcp.Components!.Correlation);
    }

    [Fact]
    public async Task Connect_OverUdp_IsDirectedAtTheHostAndPort()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Transport = PlcTransport.Udp;
        page.Host = "10.9.8.7";
        page.Port = 5020;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Connected, page.State);
        var config = Assert.IsType<UdpChannelConfig>(transports.Udp.Config);
        Assert.Equal(("10.9.8.7", 5020), (config.RemoteHost, config.RemotePort));
    }

    [Fact]
    public async Task Connect_OverSerial_Uses4CWithSequentialCorrelation()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Transport = PlcTransport.Serial;
        page.PortName = "COM20";
        page.Encoding = McEncoding.Ascii;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Connected, page.State);
        Assert.Equal("COM20", Assert.IsType<SerialChannelConfig>(transports.Serial.Config).PortName);
        Assert.Equal(CorrelationMode.Sequential, transports.Serial.Components!.Correlation);
        Assert.Equal("4C 格式 4（ASCII，串口）", page.CombinationName);
    }

    [Fact]
    public async Task AccessPathAndTimer_ReachTheRequestFrame()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.NetworkNumberHex = "0x1A";
        page.PcNumberHex = "fe";
        page.ModuleIoNumberHex = "03ff";
        page.ModuleStationNumberHex = "5";
        page.MonitoringTimerUnits = 8;
        await page.ConnectCommand.ExecuteAsync(null);
        transports.Tcp.Channel!.Responder = FakeMcDevice.Respond;
        page.Address = "D10";

        await page.ReadCommand.ExecuteAsync(null);

        byte[] request = transports.Tcp.Channel.Requests[^1];
        Assert.Equal(new byte[] { 0x50, 0x00, 0x1A, 0xFE, 0xFF, 0x03, 0x05 }, request[..7]);
        Assert.Equal(new byte[] { 0x08, 0x00 }, request[9..11]);
        Assert.True(Assert.Single(page.Operations, record => record.Operation == "读取").Succeeded);
    }

    [Fact]
    public async Task AccessPathHex_OutsideItsWidth_IsReportedInTheAddressCheck_AndRefusedOnConnect()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.NetworkNumberHex = "100";
        page.Address = "D0";

        Assert.Contains("100", page.AddressCheck);

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations).ErrorKind);
    }

    [Fact]
    public async Task SerialOnlyStationFields_DoNotBlockANetworkConnection()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        page.StationNumberHex = "ZZ";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Connected, page.State);
    }

    [Fact]
    public async Task MonitoringTimer_ShowsMilliseconds_AndWarnsWhenItReachesTheRequestTimeout()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        Assert.Equal(("1000 毫秒", string.Empty), (page.MonitoringTimerText, page.MonitoringTimerWarning));

        page.Channel.Timeouts.RequestTimeout = 1000;
        Assert.Contains("1000", page.MonitoringTimerWarning);

        page.MonitoringTimerUnits = 3;
        Assert.Equal(("750 毫秒", string.Empty), (page.MonitoringTimerText, page.MonitoringTimerWarning));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task MonitoringTimer_OutsideOneToSixtyFiveThousand_IsRefusedOnConnect(int units)
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        page.MonitoringTimerUnits = units;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations).ErrorKind);
    }

    [Fact]
    public async Task DeviceTable_UseFillsTheAddressWithTheSampleOfThatDevice()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());

        page.UseDeviceCommand.Execute(McDevices.All.Single(device => device.Symbol == "ZR"));

        Assert.Equal("ZR0", page.Address);
        Assert.Contains("ZR", page.AddressCheck);
    }

    [Fact]
    public async Task Settings_SurviveTheWorkspaceJson_AndRestoreThePage()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher, new ScriptedTransports());
        page.Transport = PlcTransport.Serial;
        page.PortName = "COM20";
        page.BaudRate = 19200;
        page.Encoding = McEncoding.Ascii;
        page.SumCheck = false;
        page.NetworkNumberHex = "02";
        page.StationNumberHex = "0A";
        page.SelfStationNumberHex = "0B";
        page.MonitoringTimerUnits = 12;
        page.Address = "D100";

        string json = JsonSerializer.Serialize<SessionSettings>(page.CaptureSettings(), WorkspaceJson.Options);
        await using var restored = CreatePage(dispatcher, new ScriptedTransports());
        restored.ApplySettings(JsonSerializer.Deserialize<SessionSettings>(json, WorkspaceJson.Options)!);

        Assert.Equal((PlcTransport.Serial, McFrameType.Frame4C, McEncoding.Ascii), (restored.Transport, restored.Frame, restored.Encoding));
        Assert.Equal(("COM20", 19200, false, 12), (restored.PortName, restored.BaudRate, restored.SumCheck, restored.MonitoringTimerUnits));
        Assert.Equal(("02", "0A", "0B"), (restored.NetworkNumberHex, restored.StationNumberHex, restored.SelfStationNumberHex));
        Assert.Equal("D100", restored.Address);
        Assert.Equal(json, JsonSerializer.Serialize<SessionSettings>(restored.CaptureSettings(), WorkspaceJson.Options));
    }
}
