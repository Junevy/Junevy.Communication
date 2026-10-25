namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>把日志条目格式化为纯文本（导出为 .txt 时使用）。报文同时写出十六进制，普通日志只有摘要。</summary>
public static class SessionLogText
{
    public static string Format(IEnumerable<SessionEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(FormatLine));

    private static string FormatLine(SessionEntry entry)
    {
        string line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff zzz} {entry.Summary}";
        return entry.HexColumn.Length == 0 ? line : $"{line}: {entry.HexColumn}";
    }
}
