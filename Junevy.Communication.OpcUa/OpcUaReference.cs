namespace Junevy.Communication.OpcUa;

/// <summary>浏览得到的一条引用。</summary>
public sealed class OpcUaReference
{
    /// <summary>
    /// 构造一条浏览结果。
    /// </summary>
    /// <param name="nodeId">目标节点的 NodeId（<c>ns=</c> 形式，可直接用于读、写与浏览）。</param>
    /// <param name="browseName">浏览名，形如 <c>命名空间索引:名称</c>，例如 <c>2:Speed</c>。</param>
    /// <param name="displayName">显示名。</param>
    /// <param name="nodeClass">节点类别。</param>
    public OpcUaReference(string nodeId, string browseName, string displayName, OpcUaNodeClass nodeClass)
    {
        NodeId = nodeId;
        BrowseName = browseName;
        DisplayName = displayName;
        NodeClass = nodeClass;
    }

    /// <summary>目标节点的 NodeId（<c>ns=</c> 形式，可直接用于读、写与浏览）。</summary>
    public string NodeId { get; }

    /// <summary>浏览名，形如 <c>命名空间索引:名称</c>，例如 <c>2:Speed</c>。</summary>
    public string BrowseName { get; }

    /// <summary>显示名。</summary>
    public string DisplayName { get; }

    /// <summary>节点类别。</summary>
    public OpcUaNodeClass NodeClass { get; }
}
