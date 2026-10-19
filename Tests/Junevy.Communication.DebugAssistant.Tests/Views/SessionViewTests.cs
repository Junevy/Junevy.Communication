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
            view.Measure(new System.Windows.Size(1000, 800));
            view.Arrange(new System.Windows.Rect(0, 0, 1000, 800));
            view.UpdateLayout();

            var panel = FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(session.LogPanel, panel.DataContext);
            Assert.NotNull(FindChild<DataGrid>(panel));
        });
    }

    private static T? FindChild<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
