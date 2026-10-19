using CommunityToolkit.Mvvm.ComponentModel;

namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>监控表的一行（计划第 7.1 节）：地址、类型、数量或长度、当前值、新值（写入用）、状态与更新时间。</summary>
public sealed partial class PlcMonitorRow : ObservableObject
{
    public PlcMonitorRow(string address, PlcValueType type, int count)
    {
        Address = address;
        Type = type;
        Count = count;
    }

    public string Address { get; }

    public PlcValueType Type { get; }

    /// <summary>数量或长度，含义见 <see cref="PlcValueType"/>。</summary>
    public int Count { get; }

    /// <summary>最近一次成功读取的值（格式化后的文字）。</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>要写入的新值，用户输入。</summary>
    [ObservableProperty]
    private string _newValue = string.Empty;

    /// <summary>"正常"，或最近一次失败的错误种类与消息。</summary>
    [ObservableProperty]
    private string _status = "未读取";

    [ObservableProperty]
    private DateTimeOffset? _updatedAt;
}
