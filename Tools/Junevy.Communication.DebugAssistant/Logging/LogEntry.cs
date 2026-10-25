using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>普通日志条目（非报文的日志）。</summary>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message, string? Exception)
    : SessionEntry(Time)
{
    public override string Kind => "日志";

    public override string Summary => Exception is null
        ? $"[{Level}] {Category}: {Message}"
        : $"[{Level}] {Category}: {Message} ({Exception})";
}
