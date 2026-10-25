using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>
/// PLC 公共面板与监控表的 XAML 能加载并绑定到页签的视图模型（计划 Task 7）。监控表从公共面板拆出，
/// 放在协议页签的底部活动区（2026-10-25）。
/// </summary>
public sealed class PlcPanelViewTests
{
    [Fact]
    public async Task PlcPanelView_LoadsAndBindsTheSession()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), new FakePlcClient());

        UiThread.Invoke(() =>
        {
            var view = new PlcPanelView { DataContext = page };
            VisualTree.LayoutExpanded(view);

            Assert.Same(page, view.DataContext);
            Assert.Null(VisualTree.FindChild<DataGrid>(view));
        });
    }

    [Fact]
    public async Task PlcMonitorView_ShowsTheMonitorRowsInADataGrid()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = new FakePlcSession(FakeServices.Create(dispatcher), new FakePlcClient());

        UiThread.Invoke(() =>
        {
            var view = new PlcMonitorView { DataContext = page };
            VisualTree.Layout(view);

            DataGrid? grid = VisualTree.FindChild<DataGrid>(view);
            Assert.NotNull(grid);
            Assert.Same(page.Monitor, grid.ItemsSource);
        });
    }
}
