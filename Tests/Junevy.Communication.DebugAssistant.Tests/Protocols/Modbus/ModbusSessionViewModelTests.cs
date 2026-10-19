using System.Text.Json;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Modbus;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Modbus;

/// <summary>
/// Modbus 页签（计划 Task 8 第 1、3 条）：通过假通道（<see cref="ScriptedTransports"/>，即测试接缝）连接库的 Modbus 客户端，
/// 检查传给库的配置与部件、站号、掩码写、读写多个寄存器、结果表格，以及对只读区域的拒绝。
/// </summary>
public sealed class ModbusSessionViewModelTests
{
    private static ModbusSessionViewModel CreatePage(ManualUiDispatcher dispatcher, ScriptedTransports transports) =>
        new(ProtocolCatalog.Get(ProtocolKind.Modbus), FakeServices.Create(dispatcher, transports: transports));

    [Fact]
    public async Task TransportAndFrame_UpdateTheCombinationAndTheChannelSettings()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        Assert.Equal("Modbus TCP", page.CombinationName);
        Assert.True(page.Channel.ShowsSocketAndTls);

        page.Frame = ModbusFrameChoice.Mbap;
        page.Transport = PlcTransport.Serial;

        Assert.Equal(ModbusFrameChoice.Rtu, page.Frame);
        Assert.Equal("Modbus RTU", page.CombinationName);
        Assert.Equal(ChannelTransport.Serial, page.Channel.Transport);
        Assert.False(page.Channel.ShowsSocketAndTls);
    }

    [Fact]
    public async Task Connect_PassesTheTransportConfig_AndTheFramingToTheLibrary()
    {
        var dispatcher = new ManualUiDispatcher();
        var transports = new ScriptedTransports();
        await using var tcp = CreatePage(dispatcher, transports);
        tcp.Host = "10.1.2.3";
        tcp.Port = 1502;

        await tcp.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Connected, tcp.State);
        var config = Assert.IsType<TcpClientChannelConfig>(transports.Tcp.Config);
        Assert.Equal(("10.1.2.3", 1502), (config.Host, config.Port));
        Assert.Equal(CorrelationMode.Keyed, transports.Tcp.Components!.Correlation);

        await using var serial = CreatePage(dispatcher, transports);
        serial.Transport = PlcTransport.Serial;
        serial.Frame = ModbusFrameChoice.Rtu;
        serial.PortName = "COM20";
        await serial.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("COM20", Assert.IsType<SerialChannelConfig>(transports.Serial.Config).PortName);
        Assert.Equal(CorrelationMode.Sequential, transports.Serial.Components!.Correlation);
    }

    [Fact]
    public async Task Udp_IsDirectedAtTheHostAndPort()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.Transport = PlcTransport.Udp;
        page.Host = "10.9.8.7";
        page.Port = 5020;

        await page.ConnectCommand.ExecuteAsync(null);

        var config = Assert.IsType<UdpChannelConfig>(transports.Udp.Config);
        Assert.Equal(("10.9.8.7", 5020), (config.RemoteHost, config.RemotePort));
    }

    [Fact]
    public async Task AnOutOfRangeRtuStation_IsRefused_WithTheLibraryMessage()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        page.Transport = PlcTransport.Serial;
        page.UnitId = 0;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
        Assert.False(string.IsNullOrWhiteSpace(record.Message));
    }

    [Fact]
    public async Task ReadTable_ShowsTheRegistersTheDeviceReturns()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        await page.ConnectCommand.ExecuteAsync(null);
        transports.Tcp.Channel!.Responder = FakeModbusDevice.Respond;
        page.TableAddress = "HR0";
        page.TableCount = 3;

        await page.ReadTableCommand.ExecuteAsync(null);

        Assert.Equal(3, page.Table.Count);
        Assert.Equal(("HR0", "0x1000", "4096"), (page.Table[0].Address, page.Table[0].Hex, page.Table[0].Unsigned));
        Assert.Equal("4098", page.Table[2].Unsigned);
        Assert.Equal("0001000000000000", page.Table[0].Binary);
    }

    [Fact]
    public async Task MaskWrite_SendsTheAndAndOrMasks_ToTheRegister()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        await page.ConnectCommand.ExecuteAsync(null);
        transports.Tcp.Channel!.Responder = FakeModbusDevice.Respond;
        page.Address = "HR5";
        page.MaskAndText = "0xFFDF";
        page.MaskOrText = "0x0020";

        await page.ApplyMaskCommand.ExecuteAsync(null);

        byte[] request = transports.Tcp.Channel.Requests[^1];
        Assert.Equal(0x16, request[7]);
        Assert.Equal(new byte[] { 0x00, 0x05, 0xFF, 0xDF, 0x00, 0x20 }, request[8..14]);
        Assert.True(Assert.Single(page.Operations, record => record.Operation == "掩码写").Succeeded);
    }

    [Fact]
    public async Task SetAndClearHelpers_FillTheMaskTexts()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        page.MaskBit = 5;

        page.SetMaskBitCommand.Execute(null);
        Assert.Equal(("0xFFDF", "0x0020"), (page.MaskAndText, page.MaskOrText));

        page.ClearMaskBitCommand.Execute(null);
        Assert.Equal(("0xFFDF", "0x0000"), (page.MaskAndText, page.MaskOrText));
    }

    [Fact]
    public async Task InvalidMaskText_IsRecordedWithoutSending()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        await page.ConnectCommand.ExecuteAsync(null);
        int before = transports.Tcp.Channel!.Requests.Count;
        page.MaskAndText = "0x10000";

        await page.ApplyMaskCommand.ExecuteAsync(null);

        Assert.Equal(before, transports.Tcp.Channel.Requests.Count);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations, record => record.Operation == "掩码写").ErrorKind);
    }

    [Fact]
    public async Task ReadWriteMultiple_ShowsTheValuesRead()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        await page.ConnectCommand.ExecuteAsync(null);
        transports.Tcp.Channel!.Responder = FakeModbusDevice.Respond;
        page.ReadWriteReadAddress = "HR3";
        page.ReadWriteReadCount = 2;
        page.ReadWriteWriteAddress = "HR14";
        page.ReadWriteWriteText = "hex:00 FF 00 FF";

        await page.ReadWriteCommand.ExecuteAsync(null);

        Assert.Equal(0x17, transports.Tcp.Channel.Requests[^1][7]);
        Assert.Equal(new[] { "0x1003", "0x1004" }, page.Table.Select(row => row.Hex));
    }

    [Theory]
    [InlineData(true, 0x06)]
    [InlineData(false, 0x10)]
    public async Task WriteOneRegister_UsesTheSingleFunction_OnlyWhenEnabled(bool singleWrite, byte expected)
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        page.UseSingleWriteFunctions = singleWrite;
        await page.ConnectCommand.ExecuteAsync(null);
        transports.Tcp.Channel!.Responder = FakeModbusDevice.Respond;
        page.Address = "HR7";
        page.ValueType = PlcValueType.Int16;
        page.WriteText = "5";

        await page.WriteCommand.ExecuteAsync(null);

        Assert.Equal(expected, transports.Tcp.Channel.Requests[^1][7]);
    }

    [Fact]
    public async Task TemporaryStation_IsSentAsTheUnitIdentifier()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        await page.ConnectCommand.ExecuteAsync(null);
        transports.Tcp.Channel!.Responder = FakeModbusDevice.Respond;
        page.TemporaryStation = 2;
        page.TableAddress = "HR0";
        page.TableCount = 1;

        await page.ReadTableCommand.ExecuteAsync(null);

        Assert.Equal(2, transports.Tcp.Channel.Requests[^1][6]);
    }

    [Fact]
    public async Task ReadOnlyArea_IsRejected_AndNothingIsSent()
    {
        var transports = new ScriptedTransports();
        await using var page = CreatePage(new ManualUiDispatcher(), transports);
        await page.ConnectCommand.ExecuteAsync(null);
        int before = transports.Tcp.Channel!.Requests.Count;
        page.Address = "IR3";
        page.WriteText = "1";

        await page.WriteCommand.ExecuteAsync(null);

        Assert.Equal(before, transports.Tcp.Channel.Requests.Count);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations, record => record.Operation == "写入").ErrorKind);
    }

    [Fact]
    public async Task FunctionHint_ShowsTheSingleWriteFunction_WhenEnabled()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        page.Address = "HR1";
        page.UseSingleWriteFunctions = true;

        Assert.Contains("06", page.FunctionHint);
    }

    [Fact]
    public async Task FunctionHint_ShowsOnlyTheMultipleFunction_WhenSingleWriteIsDisabled()
    {
        await using var page = CreatePage(new ManualUiDispatcher(), new ScriptedTransports());
        page.Address = "HR1";
        page.UseSingleWriteFunctions = false;

        Assert.Contains("10", page.FunctionHint);
        Assert.DoesNotContain("06", page.FunctionHint);
    }

    [Fact]
    public async Task Settings_SurviveTheWorkspaceJson_AndRestoreThePage()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher, new ScriptedTransports());
        page.Transport = PlcTransport.Serial;
        page.Frame = ModbusFrameChoice.Rtu;
        page.PortName = "COM20";
        page.BaudRate = 19200;
        page.Parity = System.IO.Ports.Parity.Even;
        page.UnitId = 7;
        page.UseSingleWriteFunctions = false;
        page.UseMaskWriteForBits = true;
        page.Address = "HR40";
        page.Count = 2;

        string json = JsonSerializer.Serialize<SessionSettings>(page.CaptureSettings(), WorkspaceJson.Options);
        await using var restored = CreatePage(dispatcher, new ScriptedTransports());
        restored.ApplySettings(JsonSerializer.Deserialize<SessionSettings>(json, WorkspaceJson.Options)!);

        Assert.Equal((PlcTransport.Serial, ModbusFrameChoice.Rtu, "COM20", 19200), (restored.Transport, restored.Frame, restored.PortName, restored.BaudRate));
        Assert.Equal((System.IO.Ports.Parity.Even, (byte)7, false, true), (restored.Parity, restored.UnitId, restored.UseSingleWriteFunctions, restored.UseMaskWriteForBits));
        Assert.Equal(("HR40", 2), (restored.Address, restored.Count));
        Assert.Equal(json, JsonSerializer.Serialize<SessionSettings>(restored.CaptureSettings(), WorkspaceJson.Options));
    }
}
