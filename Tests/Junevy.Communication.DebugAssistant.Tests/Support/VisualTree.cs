using System.Windows;
using System.Windows.Media;
using Junevy.Controls.Controls.Box;
using Junevy.Controls.Controls.Expander;

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

    /// <summary>
    /// 先展开视图中所有折叠的 <see cref="ExpanderPanel"/> 与 <see cref="GroupBox"/>，再排列。
    /// 折叠区域的内容不在视觉树中（日志面板、监控表等默认折叠），需要检查其中内容的测试使用本方法。
    /// </summary>
    public static void LayoutExpanded(FrameworkElement element)
    {
        ExpandCollapsed(element);
        Layout(element);
    }

    /// <summary>沿逻辑树展开全部折叠的分组：逻辑树不受折叠影响，因此能找到尚未进入视觉树的内容。</summary>
    private static void ExpandCollapsed(DependencyObject node)
    {
        switch (node)
        {
            case ExpanderPanel expander:
                expander.IsExpanded = true;
                break;
            case GroupBox group:
                group.IsCollapsed = false;
                break;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject nested)
            {
                ExpandCollapsed(nested);
            }
        }
    }
}
