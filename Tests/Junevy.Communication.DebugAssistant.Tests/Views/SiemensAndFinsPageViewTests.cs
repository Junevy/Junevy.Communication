using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.Fins;
using Junevy.Communication.DebugAssistant.Protocols.Siemens;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Fins;
using Junevy.Communication.DebugAssistant.Views.Siemens;
using Junevy.Communication.DebugAssistant.Views.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>S7 与 FINS 页的 XAML（计划 Task 10）：视图加载并绑定到页签；FINS 的区域速查表列出 18 项。</summary>
public sealed class SiemensAndFinsPageViewTests
{
    [Fact]
    public async Task S7PageView_LoadsAndBindsItsSession()
    {
        await using var page = new S7SessionViewModel(ProtocolCatalog.Get(ProtocolKind.S7), FakeServices.Create(new ManualUiDispatcher()));

        UiThread.Invoke(() =>
        {
            var view = new S7PageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }

    [Fact]
    public async Task FinsPageView_ListsTheEighteenAreas()
    {
        await using var page = new FinsSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Fins), FakeServices.Create(new ManualUiDispatcher()));

        UiThread.Invoke(() =>
        {
            var view = new FinsPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Equal(18, ((DataGrid)view.FindName("AreaGrid")!).Items.Count);
        });
    }
}
