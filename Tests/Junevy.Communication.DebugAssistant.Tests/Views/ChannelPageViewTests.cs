using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Channels;
using Junevy.Communication.DebugAssistant.Views.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>三种传输页的 XAML 能加载，绑定到各自的视图模型，并且日志面板绑定到页签自己的 <c>LogPanel</c>（计划 Task 5）。</summary>
public sealed class ChannelPageViewTests
{
    [Fact]
    public async Task TcpClientPageView_LoadsAndBindsItsSession()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.TcpPage(dispatcher, 5000);

        UiThread.Invoke(() =>
        {
            var view = new TcpClientPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            AssertLogPanelBound(view, page);
        });
    }

    [Fact]
    public async Task UdpPageView_LoadsAndBindsItsSession()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.UdpPage(dispatcher);

        UiThread.Invoke(() =>
        {
            var view = new UdpPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            AssertLogPanelBound(view, page);
        });
    }

    [Fact]
    public async Task SerialPageView_LoadsAndBindsItsSession()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.SerialPage(dispatcher);

        UiThread.Invoke(() =>
        {
            var view = new SerialPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            AssertLogPanelBound(view, page);
        });
    }

    private static void AssertLogPanelBound(System.Windows.DependencyObject view, SessionViewModel session)
    {
        LogPanel? panel = VisualTree.FindChild<LogPanel>(view);
        Assert.NotNull(panel);
        Assert.Same(session.LogPanel, panel.DataContext);
    }
}
