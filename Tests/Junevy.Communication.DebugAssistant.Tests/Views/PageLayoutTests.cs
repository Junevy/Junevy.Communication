using System.Windows;
using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.Modbus;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Channels;
using Junevy.Communication.DebugAssistant.Views.Modbus;
using Junevy.Communication.DebugAssistant.Views.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>
/// 页签的统一分区（2026-10-25）：每个页签的底部活动区都有"操作记录"与"报文与日志"，左右与上下都有可拖动的分隔条；
/// 日志面板的列显隐跟随勾选；Modbus 只显示当前传输需要的字段。
/// </summary>
public sealed class PageLayoutTests
{
    public static IEnumerable<object[]> Kinds() => ProtocolCatalog.All.Select(descriptor => new object[] { descriptor.Kind });

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryPage_HasTheActivityTabs_AndResizableAreas(ProtocolKind kind)
    {
        await using SessionViewModel page = TestShell.CreateSession(kind);

        UiThread.Invoke(() =>
        {
            FrameworkElement view = LoadPage(page);

            Assert.NotNull(VisualTree.Reveal<OperationListView>(view));
            LogPanel? log = VisualTree.Reveal<LogPanel>(view);
            Assert.NotNull(log);
            Assert.Same(page.LogPanel, log.DataContext);
            Assert.NotNull(VisualTree.FindChild<DataGrid>(log));
            Assert.NotNull(VisualTree.FindLogical<GridSplitter>(view));
        });
    }

    [Fact]
    public async Task LogPanelColumns_FollowTheDisplayOptions()
    {
        await using SessionViewModel page = TestShell.CreateSession(ProtocolKind.TcpClient);

        UiThread.Invoke(() =>
        {
            var view = new LogPanel { DataContext = page.LogPanel };
            VisualTree.Layout(view);
            var grid = (DataGrid)view.FindName("EntryGrid")!;
            Visibility Column(string header) => grid.Columns.Single(column => (string)column.Header == header).Visibility;

            Assert.Equal((Visibility.Visible, Visibility.Visible, Visibility.Collapsed), (Column("时间"), Column("HEX"), Column("ASCII")));

            page.LogPanel.ShowTimestamps = false;
            page.LogPanel.ShowHex = false;
            page.LogPanel.ShowAscii = true;

            Assert.Equal((Visibility.Collapsed, Visibility.Collapsed, Visibility.Visible), (Column("时间"), Column("HEX"), Column("ASCII")));
        });
    }

    [Fact]
    public async Task ModbusPage_ShowsOnlyTheFieldsOfTheSelectedTransport()
    {
        await using var page = (ModbusSessionViewModel)TestShell.CreateSession(ProtocolKind.Modbus);

        UiThread.Invoke(() =>
        {
            var view = new ModbusPageView { DataContext = page };
            VisualTree.Layout(view);
            var network = (FrameworkElement)view.FindName("NetworkPanel")!;
            var serial = (FrameworkElement)view.FindName("SerialPanel")!;
            Assert.Equal((Visibility.Visible, Visibility.Collapsed), (network.Visibility, serial.Visibility));

            page.Transport = PlcTransport.Serial;

            Assert.Equal((Visibility.Collapsed, Visibility.Visible), (network.Visibility, serial.Visibility));
        });
    }

    private static FrameworkElement LoadPage(SessionViewModel page)
    {
        var template = (DataTemplate)Application.Current.FindResource(new DataTemplateKey(page.GetType()));
        var view = (FrameworkElement)template.LoadContent();
        view.DataContext = page;
        VisualTree.Layout(view);
        return view;
    }
}
