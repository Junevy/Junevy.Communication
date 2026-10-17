namespace Junevy.Communication.OpcUa;

/// <summary>一次写入的目标节点与值。写入不做类型转换：<see cref="Value"/> 的 .NET 类型必须与节点的数据类型一致。</summary>
public sealed class OpcUaWriteValue
{
    /// <summary>
    /// 构造一条写入。
    /// </summary>
    /// <param name="nodeId">目标节点的 NodeId 文本。</param>
    /// <param name="value">要写入的值；类型必须与节点的数据类型一致。</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> 为 null。</exception>
    public OpcUaWriteValue(string nodeId, object value)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(value);

        NodeId = nodeId;
        Value = value;
    }

    /// <summary>目标节点的 NodeId 文本。</summary>
    public string NodeId { get; }

    /// <summary>要写入的值。</summary>
    public object Value { get; }
}
