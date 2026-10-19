using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.OpcUa;

namespace Junevy.Communication.DebugAssistant.Protocols.OpcUa;

/// <summary>
/// 读写列表的一行。读取结果逐项保存（每一项有自己的值、类型、状态与时间戳）；写入设置与写入结果也在这一行上。
/// </summary>
public sealed partial class OpcUaReadRow : ObservableObject
{
    public OpcUaReadRow(string nodeId)
    {
        NodeId = nodeId;
    }

    public string NodeId { get; }

    /// <summary>NodeId 的写法提示（ns= 形式可能随服务器重启变化）；没有提示时为空。</summary>
    public string Hint => OpcUaSupport.NodeIdHint(NodeId);

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string _dotNetType = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _sourceTimestamp = string.Empty;

    [ObservableProperty]
    private string _serverTimestamp = string.Empty;

    /// <summary>写入时使用的 .NET 类型，由用户选择；默认 Int32。</summary>
    [ObservableProperty]
    private OpcUaWriteType _writeType = OpcUaWriteType.Int32;

    /// <summary>写入一维数组（每行一个元素）。ByteString 不适用。</summary>
    [ObservableProperty]
    private bool _isArray;

    /// <summary>要写入的值的文字；空表示这一行不写。</summary>
    [ObservableProperty]
    private string _writeText = string.Empty;

    /// <summary>写入结果：服务器给出的逐项状态，或输入的解析错误。</summary>
    [ObservableProperty]
    private string _writeStatus = string.Empty;

    /// <summary>把一次读取的结果写到这一行。</summary>
    public void ApplyRead(OpcUaValue result)
    {
        Value = OpcUaSupport.FormatValue(result);
        DotNetType = OpcUaSupport.DotNetTypeName(result);
        Status = OpcUaSupport.DescribeStatus(result.Status);
        SourceTimestamp = OpcUaSupport.FormatTimestamp(result.SourceTimestamp);
        ServerTimestamp = OpcUaSupport.FormatTimestamp(result.ServerTimestamp);
    }
}

/// <summary>订阅的一项：节点与服务器给出的监控项创建状态。</summary>
public sealed record OpcUaSubscriptionRow(string NodeId, string Status);

/// <summary>实时通知列表的一行，字段都已经是界面要显示的文字。</summary>
public sealed record OpcUaNotificationRow(
    string Time,
    string NodeId,
    string Value,
    string Status,
    string SourceTime,
    string ServerTime);
