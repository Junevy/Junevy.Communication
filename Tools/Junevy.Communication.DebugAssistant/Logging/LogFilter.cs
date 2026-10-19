using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>日志面板的筛选：报文与日志各自可见，以及普通日志的最低级别。</summary>
public sealed record LogFilter(bool ShowFrames, bool ShowLogs, LogLevel MinimumLevel)
{
    public static LogFilter All { get; } = new(ShowFrames: true, ShowLogs: true, MinimumLevel: LogLevel.Debug);

    public bool Matches(SessionEntry entry) => entry switch
    {
        FrameEntry => ShowFrames,
        LogEntry log => ShowLogs && log.Level >= MinimumLevel,
        _ => true,
    };
}
