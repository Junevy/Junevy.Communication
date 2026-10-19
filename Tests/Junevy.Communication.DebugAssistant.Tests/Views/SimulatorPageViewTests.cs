using System.Windows;
using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.Simulators;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Sessions;
using Junevy.Communication.DebugAssistant.Views.Simulators;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>
/// 模拟设备页的 XAML（计划 Task 14）：视图加载并绑定到页签；内存表按存储区的种类显示对应的表格，编辑行只对字区可用；
/// 数据块的控件只在 S7 时显示。
/// </summary>
public sealed class SimulatorPageViewTests
{
    private static SimulatorSessionViewModel CreatePage(ProtocolKind kind = ProtocolKind.ModbusSlave) =>
        new(ProtocolCatalog.Get(kind), FakeServices.Create(new ManualUiDispatcher()));

    [Fact]
    public async Task SimulatorPageView_LoadsAndBindsItsSession()
    {
        await using var page = CreatePage();

        UiThread.Invoke(() =>
        {
            var view = new SimulatorPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }

    [Fact]
    public async Task MemoryTables_ShowTheKindOfTheSelectedArea()
    {
        await using var page = CreatePage();

        UiThread.Invoke(() =>
        {
            var view = new SimulatorPageView { DataContext = page };
            VisualTree.Layout(view);
            var words = (DataGrid)view.FindName("WordGrid")!;
            var bits = (DataGrid)view.FindName("BitGrid")!;
            var bytes = (DataGrid)view.FindName("ByteGrid")!;
            var editPanel = (WrapPanel)view.FindName("EditPanel")!;
            Assert.Equal((Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed, true), (words.Visibility, bits.Visibility, bytes.Visibility, editPanel.IsEnabled));

            page.SelectedArea = "C";

            Assert.Equal((Visibility.Collapsed, Visibility.Visible, Visibility.Collapsed, false), (words.Visibility, bits.Visibility, bytes.Visibility, editPanel.IsEnabled));
        });
    }

    [Fact]
    public async Task DataBlockControls_ShowOnlyOnS7()
    {
        await using var modbus = CreatePage();
        await using var s7 = CreatePage(ProtocolKind.S7Cpu);

        UiThread.Invoke(() =>
        {
            var modbusView = new SimulatorPageView { DataContext = modbus };
            VisualTree.Layout(modbusView);
            Assert.Equal(Visibility.Collapsed, ((WrapPanel)modbusView.FindName("DataBlockPanel")!).Visibility);

            var s7View = new SimulatorPageView { DataContext = s7 };
            VisualTree.Layout(s7View);
            Assert.Equal(Visibility.Visible, ((WrapPanel)s7View.FindName("DataBlockPanel")!).Visibility);
        });
    }
}
