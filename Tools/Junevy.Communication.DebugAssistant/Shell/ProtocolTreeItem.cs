using Junevy.Controls.Controls.Menu;

namespace Junevy.Communication.DebugAssistant.Shell;

/// <summary>
/// 左侧树的节点。分组节点的 <see cref="Descriptor"/> 为 null；叶节点携带对应的协议描述。
/// </summary>
public sealed class ProtocolTreeItem : TreeMenuItem
{
    public ProtocolTreeItem(string title, ProtocolDescriptor? descriptor)
    {
        Title = title;
        Descriptor = descriptor;
        Icon = descriptor?.Glyph;
    }

    /// <summary>叶节点的协议描述；分组节点为 null。</summary>
    public ProtocolDescriptor? Descriptor { get; }
}
