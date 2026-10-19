using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>页签视图的 XAML 能加载，日志面板绑定到页签自己的 <c>LogPanel</c>（计划 Task 2 第 2 条）。</summary>
public sealed class SessionViewTests
{
    [Fact]
    public async Task PlaceholderView_ShowsTheLogPanelOfItsSession()
    {
        await using SessionViewModel session = TestShell.CreateSession();

        UiThread.Invoke(() =>
        {
            var view = new PlaceholderSessionView { DataContext = session };
            VisualTree.Layout(view);

            var panel = VisualTree.FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(session.LogPanel, panel.DataContext);
            Assert.NotNull(VisualTree.FindChild<DataGrid>(panel));
        });
    }
}
