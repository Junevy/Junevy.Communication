namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>
/// 页签日志面板里的一行：报文（<see cref="FrameEntry"/>）或普通日志（<see cref="LogEntry"/>）。
/// 面板的每一列都绑定这里的一个成员，切换 HEX / ASCII 只是切换列的可见性。
/// </summary>
public abstract record SessionEntry(DateTimeOffset Time)
{
    /// <summary>类型列："报文" 或 "日志"。</summary>
    public abstract string Kind { get; }

    /// <summary>摘要列：报文的方向、长度与端点，或日志的级别、类别与消息。</summary>
    public abstract string Summary { get; }

    /// <summary>十六进制列；普通日志为空。</summary>
    public virtual string HexColumn => string.Empty;

    /// <summary>ASCII 列；普通日志为空，报文中不可打印的字节显示为 '.'。</summary>
    public virtual string AsciiColumn => string.Empty;
}
