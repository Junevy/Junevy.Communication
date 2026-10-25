using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Sessions;
using Junevy.Communication.DebugAssistant.Views.WebApi;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>
/// WebApi 三个页的 XAML（计划 Task 18）：视图加载并绑定到页签，日志面板绑定到页签的日志；服务端页的路由表显示默认路由。
/// </summary>
public sealed class WebApiPageViewTests
{
    private static ManualUiDispatcher Dispatcher() => new();

    [Fact]
    public async Task RestClientPageView_LoadsAndBindsItsSession()
    {
        await using var page = new RestClientSessionViewModel(ProtocolCatalog.Get(ProtocolKind.RestClient), FakeServices.Create(Dispatcher()));

        UiThread.Invoke(() =>
        {
            var view = new RestClientPageView { DataContext = page };
            VisualTree.LayoutExpanded(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.Reveal<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }

    [Fact]
    public async Task SoapClientPageView_LoadsAndBindsItsSession()
    {
        await using var page = new SoapClientSessionViewModel(ProtocolCatalog.Get(ProtocolKind.SoapClient), FakeServices.Create(Dispatcher()));

        UiThread.Invoke(() =>
        {
            var view = new SoapClientPageView { DataContext = page };
            VisualTree.LayoutExpanded(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.Reveal<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }

    [Fact]
    public async Task WebApiServerPageView_ShowsTheRoutesOfTheSession()
    {
        await using var page = new WebApiServerSessionViewModel(ProtocolCatalog.Get(ProtocolKind.HttpServer), FakeServices.Create(Dispatcher()));

        UiThread.Invoke(() =>
        {
            var view = new WebApiServerPageView { DataContext = page };
            VisualTree.LayoutExpanded(view);

            var routes = (DataGrid)view.FindName("RouteGrid")!;
            Assert.Equal(2, routes.Items.Count);
        });
    }
}
