using Junevy.Communication.DebugAssistant.Logging;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Logging;

/// <summary>
/// 页签的日志工厂把库的 Debug 日志变成报文与普通日志（计划 DA-D5）。这里用 <c>ILogger</c> 直接写入模板。
/// 库自己发出的报文由 <see cref="LibraryFrameLogTests"/> 验证。
/// </summary>
public sealed class SessionLoggerTests
{
    [Fact]
    public async Task StreamTxTemplate_BecomesAFrame()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        ILogger logger = session.SessionLoggerFactory.CreateLogger("Library");

        logger.LogDebug("TX {Length} bytes: {Hex}", 3, "AA-BB-CC");
        session.Log.Flush();

        var frame = Assert.IsType<FrameEntry>(Assert.Single(session.Log.Visible));
        Assert.Equal(FrameDirection.Tx, frame.Direction);
        Assert.Equal(3, frame.Length);
        Assert.Equal("AA-BB-CC", frame.Hex);
        Assert.False(frame.Truncated);
        Assert.Null(frame.Endpoint);
    }

    [Fact]
    public async Task DatagramTemplates_CarryTheirEndpoint()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        ILogger logger = session.SessionLoggerFactory.CreateLogger("Library");

        logger.LogDebug("TX {Length} bytes to {Destination}: {Hex}", 2, "192.168.0.10:502", "AA-BB");
        logger.LogDebug("RX {Length} bytes from {Remote}: {Hex}", 1, "192.168.0.11:4000", "CC");
        session.Log.Flush();

        FrameEntry[] frames = session.Log.Visible.Cast<FrameEntry>().ToArray();
        Assert.Equal(new[] { FrameDirection.Tx, FrameDirection.Rx }, frames.Select(f => f.Direction));
        Assert.Equal(new[] { "192.168.0.10:502", "192.168.0.11:4000" }, frames.Select(f => f.Endpoint));
    }

    [Fact]
    public async Task TruncatedHex_IsMarkedAndKeepsTheFullLength()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        ILogger logger = session.SessionLoggerFactory.CreateLogger("Library");

        logger.LogDebug("RX {Length} bytes: {Hex}", 300, "00-01-02...(+44 bytes)");
        session.Log.Flush();

        var frame = Assert.IsType<FrameEntry>(Assert.Single(session.Log.Visible));
        Assert.True(frame.Truncated);
        Assert.Equal(300, frame.Length);
    }

    [Fact]
    public async Task Flusher_PostsAFlush_WhileEntriesArePending()
    {
        var dispatcher = new ManualUiDispatcher();
        await using SessionViewModel session = new ScriptedSession(FakeServices.Create(dispatcher));
        session.SessionLoggerFactory.CreateLogger("Library").LogInformation("hello");

        // 后台循环每 100 ms 检查一次，有待处理条目时派发刷新。
        await Waiting.UntilAsync(() => dispatcher.Pending > 0, TimeSpan.FromSeconds(5));
        dispatcher.RunPending();

        Assert.Single(session.Log.Visible);
    }

    [Fact]
    public async Task OtherMessages_BecomeLogEntries()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        ILogger logger = session.SessionLoggerFactory.CreateLogger("Library");

        logger.LogInformation("Connected to {Host}", "plc-1");
        logger.LogDebug("TX {Length} bytes: {Hex} extra", 1, "AA");
        session.Log.Flush();

        Assert.All(session.Log.Visible, entry => Assert.IsType<LogEntry>(entry));
        LogEntry info = session.Log.Visible.Cast<LogEntry>().First();
        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Equal("Library", info.Category);
        Assert.Equal("Connected to plc-1", info.Message);
    }

    [Fact]
    public async Task Exceptions_AreSummarisedInTheLogEntry()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        ILogger logger = session.SessionLoggerFactory.CreateLogger("Library");

        logger.LogError(new InvalidOperationException("boom"), "Step {Step} failed", "read");
        session.Log.Flush();

        LogEntry entry = Assert.IsType<LogEntry>(Assert.Single(session.Log.Visible));
        Assert.Equal("Step read failed", entry.Message);
        Assert.Equal("InvalidOperationException: boom", entry.Exception);
    }
}
