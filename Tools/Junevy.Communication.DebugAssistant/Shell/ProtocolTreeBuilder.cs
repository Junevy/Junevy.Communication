using System.Collections.ObjectModel;
using Junevy.Controls.Controls.Menu;

namespace Junevy.Communication.DebugAssistant.Shell;

/// <summary>把 <see cref="ProtocolCatalog"/> 组织成左侧树（计划第 5.5 节）。</summary>
internal static class ProtocolTreeBuilder
{
    private static readonly (ProtocolGroup Group, string Title)[] GroupOrder =
    [
        (ProtocolGroup.Plc, "PLC"),
        (ProtocolGroup.Factory, "工厂系统"),
        (ProtocolGroup.Custom, "自定义协议（通道）"),
        (ProtocolGroup.Simulator, "模拟设备"),
    ];

    public static ObservableCollection<TreeMenuItem> Build()
    {
        var roots = new ObservableCollection<TreeMenuItem>();
        foreach ((ProtocolGroup group, string title) in GroupOrder)
        {
            var node = new ProtocolTreeItem(title, descriptor: null) { IsExpanded = true };
            foreach (ProtocolDescriptor descriptor in ProtocolCatalog.All.Where(d => d.Group == group))
            {
                node.Children.Add(new ProtocolTreeItem(descriptor.Title, descriptor));
            }

            roots.Add(node);
        }

        return roots;
    }
}
