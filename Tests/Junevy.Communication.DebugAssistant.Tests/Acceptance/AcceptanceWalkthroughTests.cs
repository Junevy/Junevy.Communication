using System.IO;
using System.Windows;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Fins;
using Junevy.Communication.DebugAssistant.Protocols.Melsec;
using Junevy.Communication.DebugAssistant.Protocols.Modbus;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Protocols.Siemens;
using Junevy.Communication.DebugAssistant.Protocols.Simulators;
using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Simulators.Melsec;
using Junevy.Communication.DebugAssistant.Tests.Simulators.EndToEnd;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Testing;
using Junevy.Communication.DebugAssistant.Views.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Melsec;
using Junevy.Communication.WebApi;
using Junevy.Controls.Themes;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Tests.Acceptance;

/// <summary>
/// 验收走查（计划第 31 节第 3 条，2026-10-25）：按清单逐项操作真实的页签视图模型，经回环套接字与模拟设备页签、服务端页签互连，
/// 断言结果并把界面渲染成 PNG（<see cref="AcceptanceRender"/>）。桌面上的鼠标操作（拖动分隔条的手感等）不在此列。
/// 设置 <c>JUNEVY_ACCEPTANCE_DIR</c> 后运行；设置 <c>JUNEVY_SERIAL_PAIR</c> 时另走 MC 4C 的串口组合。
/// </summary>
[Collection(SimulatorEndToEndCollection.Name)]
public sealed class AcceptanceWalkthroughTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [AcceptanceFact]
    public Task A01_NineteenTabs_RenameClose_AndTheWorkspaceIsRestored() => UiThread.RunAsync(A01_NineteenTabs_RenameClose_AndTheWorkspaceIsRestoredCore);

    private static async Task A01_NineteenTabs_RenameClose_AndTheWorkspaceIsRestoredCore()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "junevy-da-acceptance", Guid.NewGuid().ToString("N"), "workspace.json");
        var (services, _) = TestShell.Create(s => s.AddSingleton(new WorkspaceStore(workspace)));
        var vm = services.GetRequiredService<MainWindowViewModel>();
        foreach (ProtocolDescriptor descriptor in ProtocolCatalog.All)
        {
            vm.OpenSessionCommand.Execute(Leaf(vm, descriptor.Kind));
        }

        Assert.Equal(19, vm.Sessions.Count);
        RenderWindow(vm, "01-nineteen-tabs.png");

        foreach (SessionViewModel session in vm.Sessions)
        {
            session.Title += "（已改名）";
        }

        await vm.CloseSessionCommand.ExecuteAsync(vm.Sessions[^1]);
        Assert.Equal(18, vm.Sessions.Count);
        vm.SaveWorkspaceCommand.Execute(null);
        RenderWindow(vm, "02-renamed-and-one-closed.png");
        string[] titles = vm.Sessions.Select(session => session.Title).ToArray();
        await services.DisposeAsync();

        var (restoredServices, _) = TestShell.Create(s => s.AddSingleton(new WorkspaceStore(workspace)));
        var restored = restoredServices.GetRequiredService<MainWindowViewModel>();
        restored.RestoreWorkspace();

        Assert.Equal(titles, restored.Sessions.Select(session => session.Title));
        Assert.All(restored.Sessions, session => Assert.Equal(SessionState.NotConnected, session.State));
        RenderWindow(restored, "03-workspace-restored.png");
        await restoredServices.DisposeAsync();
    }

    [AcceptanceFact]
    public Task A02_MainWindow_InTheLightAndTheDarkTheme() => UiThread.RunAsync(A02_MainWindow_InTheLightAndTheDarkThemeCore);

    private static async Task A02_MainWindow_InTheLightAndTheDarkThemeCore()
    {
        var (services, _) = TestShell.Create();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        try
        {
            UiThread.Invoke(() => ThemeManager.ApplyTheme(AppTheme.Light));
            RenderWindow(vm, "04-theme-light.png");
            UiThread.Invoke(() => ThemeManager.ApplyTheme(AppTheme.Dark));
            RenderWindow(vm, "05-theme-dark.png");
        }
        finally
        {
            UiThread.Invoke(() => ThemeManager.ApplyTheme(AppTheme.Light));
            await services.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> McCombinations() =>
    [
        [McFrameType.Frame3E, McEncoding.Binary, McFrameKind.Frame3E, McEncodingKind.Binary, "3e-binary"],
        [McFrameType.Frame3E, McEncoding.Ascii, McFrameKind.Frame3E, McEncodingKind.Ascii, "3e-ascii"],
        [McFrameType.Frame4E, McEncoding.Binary, McFrameKind.Frame4E, McEncodingKind.Binary, "4e-binary"],
    ];

    [AcceptanceFact]
    public Task A03_Mc_EachFrame_ReadsAndWritesD100AndM0() => UiThread.RunAsync(A03_Mc_EachFrame_ReadsAndWritesD100AndM0Core);

    private static async Task A03_Mc_EachFrame_ReadsAndWritesD100AndM0Core()
    {
        foreach (object[] row in McCombinations())
        {
            var (frame, encoding, slaveFrame, slaveEncoding, name) =
                ((McFrameType)row[0], (McEncoding)row[1], (McFrameKind)row[2], (McEncodingKind)row[3], (string)row[4]);
            var dispatcher = new ManualUiDispatcher();
            await using SimulatorSessionViewModel slave = await StartSlaveAsync(dispatcher, ProtocolKind.McSlave, s =>
            {
                s.McFrame = slaveFrame;
                s.McEncoding = slaveEncoding;
            });
            await using var page = new McSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Mc), FakeServices.Create(dispatcher));
            page.Transport = PlcTransport.Tcp;
            page.Frame = frame;
            page.Encoding = encoding;
            await ConnectPlcAsync(page, slave.Port);

            await WriteThenReadAsync(page, "D100", PlcValueType.Int16, "1234", "1234");
            await WriteThenReadAsync(page, "M0", PlcValueType.Bool, "true", "true");
            AssertAllSucceeded(page);
            AcceptanceRender.Page(page, $"06-mc-{name}.png");
        }

        if (Environment.GetEnvironmentVariable("JUNEVY_SERIAL_PAIR") is { Length: > 0 })
        {
            await McSerial4CAsync();
        }
    }

    [AcceptanceFact]
    public Task A04_Modbus_TcpAndRtuOverTcp_MaskWriteAndReadWriteMultiple() => UiThread.RunAsync(A04_Modbus_TcpAndRtuOverTcp_MaskWriteAndReadWriteMultipleCore);

    private static async Task A04_Modbus_TcpAndRtuOverTcp_MaskWriteAndReadWriteMultipleCore()
    {
        foreach ((ModbusFrameChoice frame, bool rtu, string name) in new[] { (ModbusFrameChoice.Mbap, false, "tcp"), (ModbusFrameChoice.Rtu, true, "rtu-over-tcp") })
        {
            var dispatcher = new ManualUiDispatcher();
            await using SimulatorSessionViewModel slave = await StartSlaveAsync(dispatcher, ProtocolKind.ModbusSlave, s => s.ModbusRtu = rtu);
            await using var page = new ModbusSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Modbus), FakeServices.Create(dispatcher));
            page.Frame = frame;
            await ConnectPlcAsync(page, slave.Port);

            await WriteThenReadAsync(page, "HR5", PlcValueType.UInt16, "0x00F0", "240");
            page.MaskAndText = "0xFF0F";
            page.MaskOrText = "0x0030";
            await page.ApplyMaskCommand.ExecuteAsync(null);
            // 结果 = (0x00F0 AND 0xFF0F) OR (0x0030 AND NOT 0xFF0F) = 0x0030。
            await ReadAsync(page, "HR5", PlcValueType.UInt16, "48");

            page.ReadWriteReadAddress = "HR5";
            page.ReadWriteReadCount = 1;
            page.ReadWriteWriteAddress = "HR6";
            page.ReadWriteWriteText = "hex:00 2A";
            await page.ReadWriteCommand.ExecuteAsync(null);
            await ReadAsync(page, "HR6", PlcValueType.UInt16, "42");
            AssertAllSucceeded(page);
            Assert.Equal("48", page.Table.Single().Unsigned);
            AcceptanceRender.Page(page, $"07-modbus-{name}.png");
            AcceptanceRender.Page(page, $"07-modbus-{name}-table.png", "结果表格");
        }
    }

    [AcceptanceFact]
    public Task A05_S7_StringReadWrite_AndPutGetOffIsReported() => UiThread.RunAsync(A05_S7_StringReadWrite_AndPutGetOffIsReportedCore);

    private static async Task A05_S7_StringReadWrite_AndPutGetOffIsReportedCore()
    {
        var dispatcher = new ManualUiDispatcher();
        await using SimulatorSessionViewModel cpu = await StartSlaveAsync(dispatcher, ProtocolKind.S7Cpu, s =>
        {
            s.NewDataBlockNumber = "5";
            s.NewDataBlockSize = "64";
            s.AddDataBlockCommand.Execute(null);
        });
        await using var page = new S7SessionViewModel(ProtocolCatalog.Get(ProtocolKind.S7), FakeServices.Create(dispatcher));
        await ConnectPlcAsync(page, cpu.Port);

        // String 是单字节字符（ASCII），WString 是 UTF-16：中文要用 WString。
        page.StringAddress = "DB5.DBB0";
        page.StringMaxLength = 20;
        page.StringValue = "RECIPE A-01";
        await page.WriteStringCommand.ExecuteAsync(null);
        page.StringValue = string.Empty;
        await page.ReadStringCommand.ExecuteAsync(null);
        Assert.Equal("RECIPE A-01", page.StringValue);

        page.StringKind = S7StringKind.WString;
        page.StringAddress = "DB5.DBB22";
        page.StringMaxLength = 10;
        page.StringValue = "配方 A-01";
        await page.WriteStringCommand.ExecuteAsync(null);
        page.StringValue = string.Empty;
        await page.ReadStringCommand.ExecuteAsync(null);
        Assert.Equal("配方 A-01", page.StringValue);
        AssertAllSucceeded(page);
        AcceptanceRender.Page(page, "08-s7-string.png");

        await page.DisconnectCommand.ExecuteAsync(null);
        await cpu.DisconnectCommand.ExecuteAsync(null);
        cpu.S7PutGet = false;
        await cpu.ConnectCommand.ExecuteAsync(null);
        await page.ConnectCommand.ExecuteAsync(null);
        await page.ReadStringCommand.ExecuteAsync(null);
        OperationRecord refused = page.Operations[0];
        Assert.False(refused.Succeeded);
        Assert.Equal(CommErrorKind.RemoteError, refused.ErrorKind);
        AcceptanceRender.Page(page, "09-s7-put-get-off.png");
    }

    [AcceptanceFact]
    public Task A06_Fins_ShowsTheNodeAssignedByTheSlave() => UiThread.RunAsync(A06_Fins_ShowsTheNodeAssignedByTheSlaveCore);

    private static async Task A06_Fins_ShowsTheNodeAssignedByTheSlaveCore()
    {
        var dispatcher = new ManualUiDispatcher();
        await using SimulatorSessionViewModel slave = await StartSlaveAsync(dispatcher, ProtocolKind.FinsSlave, s => s.FinsClientNode = 0x21);
        await using var page = new FinsSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Fins), FakeServices.Create(dispatcher));
        page.ClientNode = 0;
        await ConnectPlcAsync(page, slave.Port);

        Assert.Contains("33", page.HandshakeText);
        await WriteThenReadAsync(page, "D100", PlcValueType.Int16, "-7", "-7");
        AcceptanceRender.Page(page, "10-fins-node.png");
    }

    [AcceptanceFact]
    public Task A07_Faults_NoReplyIsATimeout_AndADisconnectReconnects() => UiThread.RunAsync(A07_Faults_NoReplyIsATimeout_AndADisconnectReconnectsCore);

    private static async Task A07_Faults_NoReplyIsATimeout_AndADisconnectReconnectsCore()
    {
        var dispatcher = new ManualUiDispatcher();
        await using SimulatorSessionViewModel slave = await StartSlaveAsync(dispatcher, ProtocolKind.ModbusSlave, _ => { });
        await using var page = new ModbusSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Modbus), FakeServices.Create(dispatcher));
        page.Channel.Timeouts.RequestTimeout = 1000;
        page.Channel.Reconnect.Enabled = true;
        page.Channel.Reconnect.Mode = ReconnectMode.FixedInterval;
        page.Channel.Reconnect.Interval = 200;
        page.Options.RetryCount = 0;
        await ConnectPlcAsync(page, slave.Port);

        slave.FaultNoReply = true;
        slave.ApplyFaultsCommand.Execute(null);
        await ReadAsync(page, "HR0", PlcValueType.UInt16, expected: null);
        Assert.Equal(CommErrorKind.Timeout, page.Operations[0].ErrorKind);
        await Waiting.UntilAsync(() => page.State == SessionState.Connected, Wait, dispatcher.RunPending);

        slave.ClearFaultsCommand.Execute(null);
        slave.FaultDisconnectNext = true;
        slave.ApplyFaultsCommand.Execute(null);
        var states = new List<SessionState>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.State))
            {
                states.Add(page.State);
            }
        };
        await ReadAsync(page, "HR0", PlcValueType.UInt16, expected: null);
        await Waiting.UntilAsync(() => states.Contains(SessionState.Reconnecting) && page.State == SessionState.Connected, Wait, dispatcher.RunPending);
        await ReadAsync(page, "HR0", PlcValueType.UInt16, "0");
        AcceptanceRender.Page(page, "11-faults.png");
        AcceptanceRender.Page(slave, "12-faults-request-log.png", tab: null);
    }

    [AcceptanceFact]
    public Task A08_TcpClientAndServerPages_DelimiterFraming_KeyedCorrelation() => UiThread.RunAsync(A08_TcpClientAndServerPages_DelimiterFraming_KeyedCorrelationCore);

    private static async Task A08_TcpClientAndServerPages_DelimiterFraming_KeyedCorrelationCore()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());
        server.Settings.Framing.Mode = FramingMode.Delimiter;
        server.Settings.Framing.Delimiters = "hex:0A";
        server.AutoReplyEcho = true;
        server.AddAutoReplyCommand.Execute(null);
        await server.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, server.State);

        await using var client = ChannelTestKit.TcpPage(dispatcher, server.Settings.Port);
        client.Settings.Framing.Mode = FramingMode.Delimiter;
        client.Settings.Framing.Delimiters = "hex:0A";
        client.Settings.Correlation.Mode = CorrelationMode.Keyed;
        await client.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => server.Sessions.Count == 1, Wait, dispatcher.RunPending);

        client.PayloadText = "hex:00 01 41";
        Task first = client.RequestCommand.ExecuteAsync(null);
        client.PayloadText = "hex:00 02 42";
        Task second = client.RequestCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second).WaitAsync(Wait);
        dispatcher.RunPending();

        Assert.Equal(2, client.Operations.Count(record => record.Operation == "请求" && record.Succeeded));
        await Waiting.UntilAsync(() => server.ReceivedFrames.Count == 2, Wait, dispatcher.RunPending);
        AcceptanceRender.Page(client, "13-tcp-client-keyed.png");
        AcceptanceRender.Page(server, "14-tcp-server-echo.png");
    }

    [AcceptanceFact]
    public Task A09_HttpServerAndRestPages_EnvelopeAndDeduplication() => UiThread.RunAsync(A09_HttpServerAndRestPages_EnvelopeAndDeduplicationCore);

    private static async Task A09_HttpServerAndRestPages_EnvelopeAndDeduplicationCore()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = new WebApiServerSessionViewModel(ProtocolCatalog.Get(ProtocolKind.HttpServer), FakeServices.Create(dispatcher));
        server.EnvelopeEnabled = true;
        server.DeduplicationEnabled = true;
        AddRoute(server, "POST", "/orders", ServerRouteKind.BusinessFailure, code: "E100", message: "库存不足");
        AddRoute(server, "POST", "/reports", ServerRouteKind.Echo);
        await server.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, server.State);

        await using var rest = new RestClientSessionViewModel(ProtocolCatalog.Get(ProtocolKind.RestClient), FakeServices.Create(dispatcher));
        rest.BaseUrl = server.BoundUrls;
        rest.EnvelopeEnabled = true;
        await rest.ConnectCommand.ExecuteAsync(null);

        rest.Method = "POST";
        rest.Path = "orders";
        rest.BodyText = "{\"id\":1}";
        await rest.SendCommand.ExecuteAsync(null);
        Assert.Equal(("E100", "对端返回错误"), (rest.ResponseRemoteCode, rest.ResponseErrorKind));

        rest.EnvelopeEnabled = false;
        await rest.DisconnectCommand.ExecuteAsync(null);
        await rest.ConnectCommand.ExecuteAsync(null);
        rest.Path = "reports";
        rest.IdempotencyKey = "acceptance-1";
        await rest.SendCommand.ExecuteAsync(null);
        await rest.SendCommand.ExecuteAsync(null);
        Assert.Equal("200", rest.ResponseStatus);
        await Waiting.UntilAsync(() => server.RequestLog.Count == 3, Wait, dispatcher.RunPending);
        Assert.Equal("去重重放", server.RequestLog[0].Outcome);
        AcceptanceRender.Page(rest, "15-rest-client.png", "时间线");
        AcceptanceRender.Page(server, "16-http-server.png", "请求日志");
    }

    private static async Task McSerial4CAsync()
    {
        (string first, string second) = SerialPairEnvironment.Read();
        var dispatcher = new ManualUiDispatcher();
        await using SimulatorSessionViewModel slave = await StartSlaveAsync(dispatcher, ProtocolKind.McSlave, s =>
        {
            s.Transport = Junevy.Communication.DebugAssistant.Simulators.Hosting.SimulatorTransport.Serial;
            s.PortName = first;
            s.BaudRate = 115200;
        });
        await using var page = new McSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Mc), FakeServices.Create(dispatcher));
        page.Transport = PlcTransport.Serial;
        page.PortName = second;
        page.BaudRate = 115200;
        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Connected, page.State);

        await WriteThenReadAsync(page, "D100", PlcValueType.Int16, "1234", "1234");
        await WriteThenReadAsync(page, "M0", PlcValueType.Bool, "true", "true");
        AssertAllSucceeded(page);
        AcceptanceRender.Page(page, "06-mc-4c-serial.png");
    }

    private static async Task<SimulatorSessionViewModel> StartSlaveAsync(ManualUiDispatcher dispatcher, ProtocolKind kind, Action<SimulatorSessionViewModel> configure)
    {
        var slave = new SimulatorSessionViewModel(ProtocolCatalog.Get(kind), FakeServices.Create(dispatcher));
        slave.Host = "127.0.0.1";
        slave.Port = FreePort.Get();
        configure(slave);
        await slave.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, slave.State);
        return slave;
    }

    private static async Task ConnectPlcAsync(PlcSessionViewModel page, int port)
    {
        switch (page)
        {
            case McSessionViewModel mc:
                (mc.Host, mc.Port) = ("127.0.0.1", port);
                break;
            case ModbusSessionViewModel modbus:
                (modbus.Host, modbus.Port) = ("127.0.0.1", port);
                break;
            case S7SessionViewModel s7:
                (s7.Host, s7.Port) = ("127.0.0.1", port);
                break;
            case FinsSessionViewModel fins:
                (fins.Host, fins.Port) = ("127.0.0.1", port);
                break;
        }

        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Connected, page.State);
    }

    private static async Task WriteThenReadAsync(PlcSessionViewModel page, string address, PlcValueType type, string value, string expected)
    {
        page.Address = address;
        page.ValueType = type;
        page.Count = 1;
        page.WriteText = value;
        await page.WriteCommand.ExecuteAsync(null);
        await ReadAsync(page, address, type, expected);
    }

    private static async Task ReadAsync(PlcSessionViewModel page, string address, PlcValueType type, string? expected)
    {
        page.Address = address;
        page.ValueType = type;
        page.Count = 1;
        await page.ReadCommand.ExecuteAsync(null);
        if (expected is not null)
        {
            Assert.True(page.Operations[0].Succeeded, page.Operations[0].Message);
            Assert.Equal(expected, page.ReadText);
        }
    }

    private static void AssertAllSucceeded(SessionViewModel page) =>
        Assert.All(page.Operations, record => Assert.True(record.Succeeded, $"{record.Operation} {record.Target}: {record.Message}"));

    private static void AddRoute(WebApiServerSessionViewModel server, string method, string template, ServerRouteKind kind, string? code = null, string? message = null)
    {
        server.NewRouteMethod = method;
        server.NewRouteTemplate = template;
        server.NewRouteKind = kind;
        server.NewRouteCode = code ?? string.Empty;
        server.NewRouteMessage = message ?? string.Empty;
        server.AddRouteCommand.Execute(null);
        Assert.StartsWith("已登记", server.RouteMessage);
    }

    private static ProtocolTreeItem Leaf(MainWindowViewModel vm, ProtocolKind kind) =>
        vm.Tree.SelectMany(group => group.Children).OfType<ProtocolTreeItem>().First(item => item.Descriptor?.Kind == kind);

    private static void RenderWindow(MainWindowViewModel vm, string fileName)
    {
        FrameworkElement root = UiThread.Invoke(() =>
        {
            var window = new MainWindow { DataContext = vm };
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            return content;
        });
        AcceptanceRender.Window(root, vm, fileName);
    }
}
