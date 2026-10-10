using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Testing;

/// <summary>
/// 收集日志条目的测试日志器，同时实现 <see cref="ILogger"/> 与 <see cref="ILoggerFactory"/>。
/// 由 <see cref="CreateLogger"/> 创建的日志器与本实例共享同一个条目集合，断言"是否记录了 Warning"时使用 <see cref="GetEntries"/>。
/// 所有成员线程安全。
/// </summary>
public sealed class TestLogger : ILogger, ILoggerFactory
{
    private readonly LogSink sink;
    private readonly string categoryName;

    /// <summary>创建测试日志器（分类名为空字符串）。</summary>
    public TestLogger()
        : this(new LogSink(), string.Empty)
    {
    }

    private TestLogger(LogSink sink, string categoryName)
    {
        this.sink = sink;
        this.categoryName = categoryName;
    }

    /// <summary>所有已记录的条目（快照，按记录顺序）。</summary>
    public IReadOnlyList<TestLogEntry> Entries => sink.Snapshot();

    /// <summary>返回指定级别的条目（快照，按记录顺序）。</summary>
    /// <param name="level">日志级别。</param>
    /// <returns>该级别的条目。</returns>
    public IReadOnlyList<TestLogEntry> GetEntries(LogLevel level)
        => sink.Snapshot().Where(entry => entry.Level == level).ToList();

    /// <summary>创建与本实例共享条目集合的日志器。</summary>
    /// <param name="categoryName">分类名。</param>
    /// <returns>日志器。</returns>
    public ILogger CreateLogger(string categoryName) => new TestLogger(sink, categoryName ?? string.Empty);

    /// <summary>测试日志器不使用日志提供程序。</summary>
    /// <param name="provider">日志提供程序。</param>
    /// <exception cref="NotSupportedException">始终抛出。</exception>
    public void AddProvider(ILoggerProvider provider)
        => throw new NotSupportedException("TestLogger does not use logger providers.");

    /// <summary>无操作：条目集合由 GC 回收。</summary>
    public void Dispose()
    {
    }

    /// <summary>不支持日志作用域：返回 null。</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>除 <c>LogLevel.None</c> 外的所有级别都启用。</summary>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <summary>记录一条日志条目。</summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        sink.Add(new TestLogEntry(logLevel, categoryName, formatter(state, exception), exception));
    }

    /// <summary>线程安全的条目集合，由同一族的所有日志器共享。</summary>
    private sealed class LogSink
    {
        private readonly object sync = new object();
        private readonly List<TestLogEntry> entries = new List<TestLogEntry>();

        public void Add(TestLogEntry entry)
        {
            lock (sync)
                entries.Add(entry);
        }

        public List<TestLogEntry> Snapshot()
        {
            lock (sync)
                return new List<TestLogEntry>(entries);
        }
    }
}

/// <summary>
/// <see cref="TestLogger"/> 记录的一条日志。
/// </summary>
public sealed class TestLogEntry
{
    internal TestLogEntry(LogLevel level, string categoryName, string message, Exception? exception)
    {
        Level = level;
        CategoryName = categoryName;
        Message = message;
        Exception = exception;
    }

    /// <summary>日志级别。</summary>
    public LogLevel Level { get; }

    /// <summary>分类名。</summary>
    public string CategoryName { get; }

    /// <summary>格式化后的消息文本。</summary>
    public string Message { get; }

    /// <summary>附带的异常；没有时为 null。</summary>
    public Exception? Exception { get; }
}
