using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Simulators;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Testing;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Simulators;

/// <summary>
/// 模拟设备页签（计划 Task 14 第 1 条）：启动与停止开关监听，不合法的站号拒绝启动，设置往返，故障注入的校验，
/// 内存表的区列表与分页，字的写入校验，S7 数据块的新建与字节区的分页。位区与字节区的数值由端到端测试验证（见 PageEndToEndTests）。
/// </summary>
public sealed class SimulatorSessionViewModelTests
{
    private static SimulatorSessionViewModel CreatePage(ProtocolKind kind = ProtocolKind.ModbusSlave) =>
        new(ProtocolCatalog.Get(kind), FakeServices.Create(new ManualUiDispatcher()));

    [Fact]
    public async Task Start_OpensTheListener_AndStop_ClosesIt()
    {
        int port = FreePort.Get();
        await using var page = CreatePage();
        page.Port = port;

        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, page.State);
        using (var client = new TcpClient("127.0.0.1", port))
        {
            Assert.True(client.Connected);
        }

        await page.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Stopped, page.State);
        Assert.Throws<SocketException>(() => new TcpClient("127.0.0.1", port));
    }

    [Fact]
    public async Task AnIllegalUnitId_IsRefusedOnStart()
    {
        await using var page = CreatePage();
        page.UnitIds = "1,x";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        Assert.Equal(CommErrorKind.InvalidRequest, Assert.Single(page.Operations).ErrorKind);
    }

    [Fact]
    public async Task Settings_RoundTripThroughTheSavedRecord()
    {
        await using var page = CreatePage();
        page.Transport = SimulatorTransport.Udp;
        page.Host = "127.0.0.1";
        page.Port = 5020;
        page.UnitIds = "1,2";
        page.ModbusRtu = true;
        SessionSettings saved = page.CaptureSettings();

        await using var other = CreatePage();
        other.ApplySettings(saved);

        Assert.Equal(saved, other.CaptureSettings());
        Assert.Equal(SimulatorTransport.Udp, other.Transport);
    }

    [Fact]
    public async Task FaultSettings_AreValidatedBeforeTheyApply()
    {
        await using var page = CreatePage();

        page.FaultDelayMilliseconds = 60001;
        page.ApplyFaultsCommand.Execute(null);
        Assert.Equal("应答延迟必须在 0 到 60000 毫秒之间。", page.FaultMessage);

        page.FaultDelayMilliseconds = 0;
        page.FaultForcedErrorText = "0x10000";
        page.ApplyFaultsCommand.Execute(null);
        Assert.Equal("强制错误码必须是 0 到 0xFFFF 之间的十进制或 0x 开头的十六进制数。", page.FaultMessage);

        page.FaultForcedErrorText = "0xC051";
        page.ApplyFaultsCommand.Execute(null);
        Assert.Equal("故障注入已设置。", page.FaultMessage);

        page.ClearFaultsCommand.Execute(null);
        Assert.Equal((string.Empty, 0, "故障注入已清除。"), (page.FaultForcedErrorText, page.FaultDelayMilliseconds, page.FaultMessage));
    }

    [Fact]
    public async Task Modbus_ListsItsAreas_WordsFirst()
    {
        await using var page = CreatePage();

        Assert.Equal(new[] { "HR", "IR", "C", "DI" }, page.MemoryAreas);
        Assert.Equal(("HR", true, 64), (page.SelectedArea, page.IsWordArea, page.WordRows.Count));
        Assert.Equal("0 至 63（共 65536 个字，第 1/1024 页）", page.MemoryPageText);
    }

    [Fact]
    public async Task Paging_MovesByOnePageAndStopsAtTheFirst()
    {
        await using var page = CreatePage();

        page.PreviousPageCommand.Execute(null);
        Assert.Equal(0, page.PageIndex);

        page.NextPageCommand.Execute(null);
        Assert.Equal((1, 64), (page.PageIndex, page.WordRows[0].Address));
        Assert.Equal("64 至 127（共 65536 个字，第 2/1024 页）", page.MemoryPageText);

        page.SelectedArea = "IR";
        Assert.Equal(0, page.PageIndex);
    }

    [Fact]
    public async Task WordWrites_ValidateTheAddressAndTheValue_AndShowTheNewValue()
    {
        await using var page = CreatePage();

        page.EditAddress = 3;
        page.EditValue = "0x00FF";
        page.WriteWordCommand.Execute(null);

        SimulatorMemoryRow row = page.WordRows[3];
        Assert.Equal((3, (ushort)255, "0x00FF", "0000000011111111"), (row.Address, row.Unsigned, row.Hex, row.Binary));
        Assert.Equal(string.Empty, page.MemoryMessage);

        page.EditValue = "70000";
        page.WriteWordCommand.Execute(null);
        Assert.Equal("写入的值必须是 0 到 0xFFFF 之间的十进制或 0x 开头的十六进制数。", page.MemoryMessage);

        page.EditValue = "1";
        page.EditAddress = 70000;
        page.WriteWordCommand.Execute(null);
        Assert.Equal("地址必须在 0 到 65535 之间。", page.MemoryMessage);
    }

    [Fact]
    public async Task BitAreas_ShowSixteenBitsPerRow_AndAreNotWordAreas()
    {
        await using var page = CreatePage();

        page.SelectedArea = "C";

        Assert.Equal((true, false, false), (page.IsBitArea, page.IsWordArea, page.IsByteArea));
        Assert.Equal(64, page.BitRows.Count);
        Assert.Equal(string.Join(' ', Enumerable.Repeat("0", 16)), page.BitRows[0].Bits);
        Assert.Equal(16, page.BitRows[1].Address);
        Assert.Equal("0 至 1023（共 65536 个位，第 1/64 页）", page.MemoryPageText);
    }

    [Fact]
    public async Task S7_ShowsItsByteAreas_AndCreatesDataBlocks()
    {
        await using var page = CreatePage(ProtocolKind.S7Cpu);
        Assert.Contains("M", page.MemoryAreas);

        page.SelectedArea = "M";
        Assert.Equal((false, false, true, 64), (page.IsWordArea, page.IsBitArea, page.IsByteArea, page.ByteRows.Count));
        Assert.Equal(string.Join(' ', Enumerable.Repeat("00", 16)), page.ByteRows[0].Bytes);

        page.NewDataBlockNumber = "1";
        page.NewDataBlockSize = "16";
        page.AddDataBlockCommand.Execute(null);
        Assert.Equal("已创建 DB1（16 字节）。", page.DataBlockMessage);
        Assert.Equal(("DB1", true, 1), (page.SelectedArea, page.MemoryAreas.Contains("DB1"), page.ByteRows.Count));
        Assert.Equal("0 至 15（共 16 个字节，第 1/1 页）", page.MemoryPageText);

        page.AddDataBlockCommand.Execute(null);
        Assert.Equal("DB1 已经存在。", page.DataBlockMessage);

        page.NewDataBlockNumber = "0";
        page.AddDataBlockCommand.Execute(null);
        Assert.Equal("数据块号必须在 1 到 65535 之间。", page.DataBlockMessage);

        page.NewDataBlockNumber = "2";
        page.NewDataBlockSize = "65537";
        page.AddDataBlockCommand.Execute(null);
        Assert.Equal("数据块的大小必须在 1 到 65536 字节之间。", page.DataBlockMessage);
    }
}
