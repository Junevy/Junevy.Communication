using Junevy.Communication.DebugAssistant.Protocols.Ftp;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Ftp;
using Junevy.Communication.DebugAssistant.Views.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>FTP/FTPS 页的 XAML（计划 Task 19）：视图加载并绑定到页签，日志面板绑定到页签自己的日志。</summary>
public sealed class FtpPageViewTests
{
    [Fact]
    public async Task FtpPageView_LoadsAndBindsItsSession()
    {
        await using var page = new FtpSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Ftp), FakeServices.Create(new ManualUiDispatcher()));

        UiThread.Invoke(() =>
        {
            var view = new FtpPageView { DataContext = page };
            VisualTree.LayoutExpanded(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }
}
