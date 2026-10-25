using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.OpcUa;

namespace Junevy.Communication.DebugAssistant.Protocols.OpcUa;

/// <summary>
/// 浏览树的一个节点（计划 7.8 节）。懒加载：只有展开时才浏览子节点；对象节点在未加载前有一个占位子节点，用来显示展开箭头。
/// 变量节点是叶子（属性节点不在本页浏览），双击加入读写列表。
/// </summary>
public sealed partial class OpcUaNodeItem : ObservableObject
{
    private readonly Func<OpcUaNodeItem, Task> _loadChildren;

    public OpcUaNodeItem(
        string nodeId,
        string browseName,
        string displayName,
        OpcUaNodeClass nodeClass,
        Func<OpcUaNodeItem, Task> loadChildren)
        : this(nodeId, browseName, displayName, nodeClass, loadChildren, isPlaceholder: false)
    {
    }

    private OpcUaNodeItem(
        string nodeId,
        string browseName,
        string displayName,
        OpcUaNodeClass nodeClass,
        Func<OpcUaNodeItem, Task> loadChildren,
        bool isPlaceholder)
    {
        NodeId = nodeId;
        BrowseName = browseName;
        DisplayName = displayName;
        NodeClass = nodeClass;
        IsPlaceholder = isPlaceholder;
        _loadChildren = loadChildren;
        if (CanHaveChildren)
        {
            Children.Add(new OpcUaNodeItem(string.Empty, string.Empty, "…", OpcUaNodeClass.Unspecified, loadChildren, isPlaceholder: true));
        }
    }

    /// <summary>节点 ID，ns= 形式（浏览返回的形式，可以直接用于读写）。</summary>
    public string NodeId { get; }

    public string BrowseName { get; }

    public string DisplayName { get; }

    public OpcUaNodeClass NodeClass { get; }

    /// <summary>占位子节点（对象节点未加载时的展开箭头），没有 NodeId。</summary>
    public bool IsPlaceholder { get; }

    public bool IsVariable => !IsPlaceholder && NodeClass == OpcUaNodeClass.Variable;

    /// <summary>对象节点有子节点，展开时才加载。</summary>
    public bool CanHaveChildren => !IsPlaceholder && NodeClass is OpcUaNodeClass.Object or OpcUaNodeClass.ObjectType;

    /// <summary>子节点是否已经从服务器加载过（浏览失败时为 false，下次展开会重试）。</summary>
    public bool IsLoaded { get; private set; }

    public ObservableCollection<OpcUaNodeItem> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLoading;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsLoaded && !IsLoading)
        {
            // 加载器不抛出异常（失败记录在消息区），因此这里不等待也不观察结果。
            _ = _loadChildren(this);
        }
    }

    /// <summary>用浏览的结果替换子节点。</summary>
    internal void ApplyChildren(IReadOnlyList<OpcUaNodeItem> children)
    {
        Children.Clear();
        foreach (OpcUaNodeItem child in children)
        {
            Children.Add(child);
        }

        IsLoaded = true;
        IsLoading = false;
    }

    /// <summary>浏览失败或已失效：保留占位子节点，以便下次展开时重试。</summary>
    internal void FailLoad() => IsLoading = false;
}
