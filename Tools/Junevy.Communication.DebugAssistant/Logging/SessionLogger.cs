using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>把日志写入页签的 <see cref="SessionLog"/>：报文成为 <see cref="FrameEntry"/>，其余成为 <see cref="LogEntry"/>。</summary>
internal sealed class SessionLogger(string category, SessionLog log, TimeProvider clock) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        DateTimeOffset now = clock.GetLocalNow();
        if (FrameLogParser.TryParse(state, now, out FrameEntry? frame))
        {
            log.Append(frame);
            return;
        }

        string? exceptionSummary = exception is null ? null : $"{exception.GetType().Name}: {exception.Message}";
        log.Append(new LogEntry(now, logLevel, category, formatter(state, exception), exceptionSummary));
    }
}
