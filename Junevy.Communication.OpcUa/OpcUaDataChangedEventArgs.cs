namespace Junevy.Communication.OpcUa;

/// <summary>一条数据变更通知（P62-D10）。</summary>
public sealed class OpcUaDataChangedEventArgs : EventArgs
{
    /// <summary>
    /// 构造一条通知。
    /// </summary>
    /// <param name="nodeId">订阅时传入的 NodeId 文本，原样返回。</param>
    /// <param name="value">读到的值，含状态码与时间戳。</param>
    public OpcUaDataChangedEventArgs(string nodeId, OpcUaValue value)
    {
        NodeId = nodeId;
        Value = value;
    }

    /// <summary>订阅时传入的 NodeId 文本，原样返回（不换算成 <c>ns=</c> 形式）。</summary>
    public string NodeId { get; }

    /// <summary>读到的值，含状态码与时间戳。</summary>
    public OpcUaValue Value { get; }
}
