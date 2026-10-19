using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>页签私有的日志提供程序（计划 DA-D5）：每个页签的 <c>ILoggerFactory</c> 挂一个，互不影响。</summary>
internal sealed class SessionLogSink(SessionLog log, TimeProvider clock) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SessionLogger(categoryName, log, clock);

    public void Dispose()
    {
    }
}
