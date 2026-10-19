namespace Junevy.Communication.DebugAssistant.Protocols.Secs;

/// <summary>
/// SML 文本解析失败（计划 DA-D13）。消息以"第 N 行第 M 列"开头，行号与列号都从 1 开始。
/// </summary>
public sealed class SmlParseException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="detail">失败原因。</param>
    /// <param name="line">出错的行号，从 1 开始。</param>
    /// <param name="column">出错的列号，从 1 开始。</param>
    public SmlParseException(string detail, int line, int column)
        : base($"第 {line} 行第 {column} 列：{detail}")
    {
        Line = line;
        Column = column;
    }

    /// <summary>出错的行号，从 1 开始。</summary>
    public int Line { get; }

    /// <summary>出错的列号，从 1 开始。</summary>
    public int Column { get; }
}
