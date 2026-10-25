using System.Windows;
using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
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

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryPage_CanBeDisposedTwice(ProtocolKind kind)
    {
        // 关闭页签释放一次，宿主退出时 DI 容器还会再释放一次（2026-10-25 验收走查发现第二次释放抛出异常）。
        SessionViewModel page = TestShell.CreateSession(kind);

        await page.DisposeAsync();
        await page.DisposeAsync();
    }

    [Fact]
    public Task TcpServerPage_ShowsAConnectedSession() => UiThread.RunAsync(TcpServerPageShowsAConnectedSessionAsync);

    // 在界面线程上运行：视图绑定的集合只能由创建视图的线程修改（与应用相同）。
    private static async Task TcpServerPageShowsAConnectedSessionAsync()
    {
        // 会话表的 TLS 列与自动应答表的回显列绑定只读属性：复选框列默认双向绑定，出现第一行时抛出异常（2026-10-25 验收走查发现）。
        var dispatcher = new ManualUiDispatcher();
        await using var server = ChannelTestKit.TcpServerPage(dispatcher, FreePort.Get());
        server.AddAutoReplyCommand.Execute(null);
        await server.ConnectCommand.ExecuteAsync(null);
        await using var client = ChannelTestKit.TcpPage(dispatcher, server.Settings.Port);
        await client.ConnectCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => server.Sessions.Count == 1, TimeSpan.FromSeconds(5), dispatcher.RunPending);

        var view = new TcpServerPageView { DataContext = server };
        VisualTree.LayoutExpanded(view);

        // 会话表与自动应答表各有一行（复选框列都已生成）。
        Assert.Equal(1, CountRows<ServerSession>(view));
        Assert.Equal(1, CountRows<AutoReplyRule>(view));
    }

    private static int CountRows<TItem>(DependencyObject root)
    {
        int rows = 0;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            rows += child is DataGridRow { Item: TItem } ? 1 : CountRows<TItem>(child);
        }

        return rows;
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
