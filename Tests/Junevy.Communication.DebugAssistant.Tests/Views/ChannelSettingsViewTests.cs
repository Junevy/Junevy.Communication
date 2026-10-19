using System.Windows;
using System.Windows.Controls;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Channels;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>通道参数视图的 XAML 能加载，绑定到编辑器；分帧参数随模式显示（计划 Task 4 第 1 条）。</summary>
public sealed class ChannelSettingsViewTests
{
    [Fact]
    public void View_LoadsAndShowsOnlyTheFramingParametersOfTheSelectedMode()
    {
        var settings = new ChannelSettingsEditor();

        UiThread.Invoke(() =>
        {
            var view = new ChannelSettingsView { DataContext = settings };
            view.Measure(new Size(900, 1200));
            view.Arrange(new Rect(0, 0, 900, 1200));
            view.UpdateLayout();

            Assert.NotNull(view.Content);
            StackPanel delimiterPanel = Assert.IsType<StackPanel>(FindPanelWith(view, "hex:0D 0A"));
            Assert.Equal(Visibility.Collapsed, delimiterPanel.Visibility);

            settings.Framing.Mode = FramingMode.Delimiter;
            view.UpdateLayout();
            Assert.Equal(Visibility.Visible, delimiterPanel.Visibility);
        });
    }

    /// <summary>在逻辑树中找到包含某段文本的 TextBox 的 StackPanel（分帧的一个模式面板）。</summary>
    private static StackPanel? FindPanelWith(DependencyObject node, string text)
    {
        if (node is StackPanel panel && panel.Children.OfType<TextBox>().Any(box => box.Text == text))
        {
            return panel;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject nested && FindPanelWith(nested, text) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
