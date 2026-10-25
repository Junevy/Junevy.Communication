using System.Windows;
using TabItem = System.Windows.Controls.TabItem;
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

    /// <summary>
    /// 让逻辑树中第一个 <typeparamref name="T"/> 显示出来并返回它：选中它所在的页签（底部活动区每次只显示一个页签的内容）、
    /// 展开它所在的分组，再排列。找不到时返回 null。
    /// </summary>
    public static T? Reveal<T>(FrameworkElement root)
        where T : DependencyObject
    {
        T? target = FindLogical<T>(root);
        if (target is null)
        {
            return null;
        }

        for (DependencyObject? node = target; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            switch (node)
            {
                case TabItem tab:
                    tab.IsSelected = true;
                    break;
                case GroupBox group:
                    group.IsCollapsed = false;
                    break;
                case ExpanderPanel expander:
                    expander.IsExpanded = true;
                    break;
            }
        }

        Layout(root);
        return target;
    }

    /// <summary>在逻辑树中查找第一个指定类型的后代（包括未选中页签与折叠分组里的内容）。</summary>
    public static T? FindLogical<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match)
            {
                return match;
            }

            if (child is DependencyObject node && FindLogical<T>(node) is { } nested)
            {
                return nested;
            }
        }

        return null;
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
