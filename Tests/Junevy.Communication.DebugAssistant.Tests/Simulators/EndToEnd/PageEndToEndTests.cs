using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Simulators;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Siemens;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators.EndToEnd;

/// <summary>
/// 模拟设备页签与库的客户端（计划 Task 14 第 2 条）：页签自己创建的引擎与存储区接受客户端的请求，页签的表格显示结果；
/// 页签上的写入同样到达客户端。位区、字节区与数据块都通过页签的表格验证。
/// </summary>
[Collection(SimulatorEndToEndCollection.Name)]
public sealed class PageEndToEndTests
{
    [Fact]
    public async Task ModbusTcpPage_ShowsTheClientWrites_AndSendsItsOwnWrites()
    {
        int port = FreePort.Get();
        await using var page = CreatePage(ProtocolKind.ModbusSlave);
        page.Port = port;
        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, page.State);

        await using var client = ModbusClient.Create(
            new TcpClientChannelConfig { Host = "127.0.0.1", Port = port, RequestTimeout = 2000 },
            new ModbusClientOptions { Framing = ModbusFraming.Mbap, UnitId = 1 });
        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.True((await client.WriteUInt16Async("HR100", new ushort[] { 0x1234 })).IsSuccess);
        Assert.True((await client.WriteBitsAsync("C5", new[] { true })).IsSuccess);

        page.PageIndex = 1;
        page.RefreshMemoryCommand.Execute(null);
        Assert.Equal(0x1234, page.WordRows.Single(row => row.Address == 100).Unsigned);

        page.SelectedArea = "C";
        Assert.Equal("0 0 0 0 0 1 0 0 0 0 0 0 0 0 0 0", page.BitRows[0].Bits);

        page.SelectedArea = "HR";
        page.EditAddress = 7;
        page.EditValue = "0x0042";
        page.WriteWordCommand.Execute(null);
        CommResult<ushort[]> read = await client.ReadUInt16Async("HR7", 1);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(new ushort[] { 0x0042 }, read.Data);
    }

    [SerialPairFact]
    public async Task ModbusSerialPage_AnswersRtuThroughThePageEngine()
    {
        (string first, string second) = SerialPairEnvironment.Read();
        await using var page = CreatePage(ProtocolKind.ModbusSlave);
        page.Transport = SimulatorTransport.Serial;
        page.PortName = first;
        page.BaudRate = 115200;
        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, page.State);

        await using var client = ModbusClient.Create(
            new SerialChannelConfig { PortName = second, BaudRate = 115200, RequestTimeout = 2000 },
            new ModbusClientOptions { Framing = ModbusFraming.Rtu, UnitId = 1 });
        Assert.True((await client.ConnectAsync()).IsSuccess);

        page.SelectedArea = "HR";
        page.EditAddress = 0;
        page.EditValue = "0x00AA";
        page.WriteWordCommand.Execute(null);

        CommResult<ushort[]> read = await client.ReadUInt16Async("HR0", 1);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(new ushort[] { 0x00AA }, read.Data);
    }

    [Fact]
    public async Task S7Page_DataBlockWrittenByTheClient_ShowsInItsByteTable()
    {
        int port = FreePort.Get();
        await using var page = CreatePage(ProtocolKind.S7Cpu);
        page.Port = port;
        page.NewDataBlockNumber = "1";
        page.NewDataBlockSize = "16";
        page.AddDataBlockCommand.Execute(null);
        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, page.State);

        await using var client = S7Client.Create(
            new TcpClientChannelConfig { Host = "127.0.0.1", Port = port, RequestTimeout = 2000 },
            new S7ClientOptions());
        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.True((await client.WriteUInt16Async("DB1.DBW0", new ushort[] { 0x1234 })).IsSuccess);

        page.SelectedArea = "DB1";
        page.RefreshMemoryCommand.Execute(null);
        Assert.StartsWith("12 34 ", page.ByteRows[0].Bytes);
    }

    private static SimulatorSessionViewModel CreatePage(ProtocolKind kind) =>
        new(ProtocolCatalog.Get(kind), FakeServices.Create(new ManualUiDispatcher()));
}
