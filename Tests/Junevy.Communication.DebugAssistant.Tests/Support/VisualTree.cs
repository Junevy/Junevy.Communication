using System.Windows;
using System.Windows.Media;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>视觉树测试的辅助方法（必须在 STA 线程上调用）。</summary>
internal static class VisualTree
{
    /// <summary>在视觉树中查找第一个指定类型的后代。</summary>
    public static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
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

    /// <summary>按固定大小度量、排列并更新布局，使绑定与模板生效。</summary>
    public static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(1000, 800));
        element.Arrange(new Rect(0, 0, 1000, 800));
        element.UpdateLayout();
    }
}
