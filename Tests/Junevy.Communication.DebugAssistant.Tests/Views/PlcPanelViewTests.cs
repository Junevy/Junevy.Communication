using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>PLC 公共面板的 XAML 能加载，绑定到页签的视图模型，并且监控表是一个 <see cref="DataGrid"/>（计划 Task 7）。</summary>
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
            Assert.NotNull(VisualTree.FindChild<DataGrid>(view));
        });
    }
}
