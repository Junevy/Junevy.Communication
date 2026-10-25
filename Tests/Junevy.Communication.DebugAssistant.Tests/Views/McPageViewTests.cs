using System.Windows;
using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.Melsec;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Melsec;
using Junevy.Communication.DebugAssistant.Views.Sessions;
using Junevy.Communication.Melsec;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>
/// MC 页的 XAML（计划 Task 9）：视图加载并绑定到页签；帧列表随传输变化，选中的帧保持合法；4C 的站号与和校验只在串口时可用；
/// 软元件速查表列出 28 项。
/// </summary>
public sealed class McPageViewTests
{
    private static McSessionViewModel CreatePage(ManualUiDispatcher dispatcher) =>
        new(ProtocolCatalog.Get(ProtocolKind.Mc), FakeServices.Create(dispatcher));

    [Fact]
    public async Task McPageView_LoadsAndBindsItsSession()
    {
        await using var page = CreatePage(new ManualUiDispatcher());

        UiThread.Invoke(() =>
        {
            var view = new McPageView { DataContext = page };
            VisualTree.LayoutExpanded(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.Reveal<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
            Assert.Equal(28, ((DataGrid)view.FindName("DeviceGrid")!).Items.Count);
        });
    }

    [Fact]
    public async Task FrameBox_FollowsTheTransport_AndKeepsALegalFrameSelected()
    {
        await using var page = CreatePage(new ManualUiDispatcher());

        UiThread.Invoke(() =>
        {
            var view = new McPageView { DataContext = page };
            VisualTree.LayoutExpanded(view);
            var frames = (ComboBox)view.FindName("FrameBox")!;
            Assert.Equal(2, frames.Items.Count);

            page.Transport = PlcTransport.Serial;

            Assert.Single(frames.Items);
            Assert.Equal((McFrameType.Frame4C, McFrameType.Frame4C), (page.Frame, (McFrameType)frames.SelectedItem!));

            page.Transport = PlcTransport.Tcp;

            Assert.Equal(2, frames.Items.Count);
            Assert.Equal((McFrameType.Frame3E, McFrameType.Frame3E), (page.Frame, (McFrameType)frames.SelectedItem!));
        });
    }

    [Fact]
    public async Task SerialOnlyFields_AreOffUntilSerialIsSelected()
    {
        await using var page = CreatePage(new ManualUiDispatcher());

        UiThread.Invoke(() =>
        {
            var view = new McPageView { DataContext = page };
            VisualTree.LayoutExpanded(view);
            var sumCheck = (CheckBox)view.FindName("SumCheckBox")!;
            var serialStations = (StackPanel)view.FindName("SerialStationPanel")!;
            Assert.Equal((false, Visibility.Collapsed), (sumCheck.IsEnabled, serialStations.Visibility));

            page.Transport = PlcTransport.Serial;

            Assert.Equal((true, Visibility.Visible), (sumCheck.IsEnabled, serialStations.Visibility));
        });
    }
}
