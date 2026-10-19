using Junevy.Communication.DebugAssistant.Logging;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Logging;

public sealed class SessionLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Flush_MovesPendingEntriesToVisible_InOrder()
    {
        var log = new SessionLog();
        log.Append(Info("first"));
        log.Append(Info("second"));
        Assert.Empty(log.Visible);

        log.Flush();

        Assert.Equal(new[] { "first", "second" }, log.Visible.Cast<LogEntry>().Select(e => e.Message));
    }

    [Fact]
    public void Capacity_KeepsTheNewestEntries_InBothThePendingQueueAndTheVisibleList()
    {
        var log = new SessionLog(capacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            log.Append(Info($"entry {i}"));
        }

        log.Flush();

        Assert.Equal(3, log.Capacity);
        Assert.Equal(new[] { "entry 3", "entry 4", "entry 5" }, log.Visible.Cast<LogEntry>().Select(e => e.Message));
    }

    [Fact]
    public void Pause_KeepsEntriesPending_UntilResumed()
    {
        var log = new SessionLog();
        log.IsPaused = true;
        log.Append(Info("held"));

        log.Flush();

        Assert.Empty(log.Visible);
        Assert.True(log.HasPending);

        log.IsPaused = false;
        log.Flush();

        Assert.Single(log.Visible);
        Assert.False(log.HasPending);
    }

    [Fact]
    public void Filter_HidesFramesAndLowLevelLogs_AndRebuildsTheVisibleList()
    {
        var log = new SessionLog();
        log.Append(Info("debug line", LogLevel.Debug));
        log.Append(Info("warning line", LogLevel.Warning));
        log.Append(Tx(3));
        log.Flush();

        log.Filter = LogFilter.All with { ShowFrames = false };
        Assert.Equal(new[] { "debug line", "warning line" }, log.Visible.OfType<LogEntry>().Select(e => e.Message));

        log.Filter = LogFilter.All with { ShowFrames = true, ShowLogs = true, MinimumLevel = LogLevel.Warning };
        Assert.Equal(new[] { "warning line" }, log.Visible.OfType<LogEntry>().Select(e => e.Message));
        Assert.Single(log.Visible.OfType<FrameEntry>());
    }

    [Fact]
    public void Clear_EmptiesTheVisibleList_ButKeepsTheStatistics()
    {
        var log = new SessionLog();
        log.Append(Tx(3));
        log.Append(Info("line"));
        log.Flush();

        log.Clear();

        Assert.Empty(log.Visible);
        Assert.False(log.HasPending);
        Assert.Equal(1, log.Statistics.TxFrames);
    }

    [Fact]
    public void Statistics_CountFramesBytesAndTheLastTimes_WithoutWaitingForFlush()
    {
        var log = new SessionLog(capacity: 2);
        log.Append(Tx(3));
        log.Append(Rx(5));
        log.Append(Tx(2));
        log.Append(Info("not a frame"));

        FrameStatistics stats = log.Statistics;

        Assert.Equal(2, stats.TxFrames);
        Assert.Equal(5, stats.TxBytes);
        Assert.Equal(1, stats.RxFrames);
        Assert.Equal(5, stats.RxBytes);
        Assert.Equal(Start, stats.LastTx);
        Assert.Equal(Start + TimeSpan.FromSeconds(1), stats.LastRx);
    }

    [Fact]
    public void Changed_IsRaisedByFlushAndFilterChanges_ButNotByAnEmptyFlush()
    {
        var log = new SessionLog();
        int raised = 0;
        log.Changed += (_, _) => raised++;

        log.Flush();
        Assert.Equal(0, raised);

        log.Append(Info("line"));
        log.Flush();
        Assert.Equal(1, raised);

        log.Filter = LogFilter.All with { ShowLogs = false };
        Assert.Equal(2, raised);
    }

    [Fact]
    public void FrameSummary_ShowsDirectionLengthEndpointAndTruncation()
    {
        var frame = new FrameEntry(Start, FrameDirection.Rx, 300, "00-01-02...(+44 bytes)", Truncated: true, Endpoint: "10.0.0.2:502");

        Assert.Equal("接收 300 字节 来自 10.0.0.2:502（已截断，共 300 字节）", frame.Summary);
        Assert.Equal("00-01-02...(+44 bytes)", frame.HexColumn);
        Assert.Equal("（已截断）", frame.AsciiColumn);
    }

    [Fact]
    public void FrameAsciiColumn_ReplacesNonPrintableBytesWithDots()
    {
        var frame = new FrameEntry(Start, FrameDirection.Tx, 4, "48-69-00-7F", Truncated: false, Endpoint: null);

        Assert.Equal("Hi..", frame.AsciiColumn);
    }

    [Fact]
    public void LogEntry_HasNoHexOrAsciiColumns()
    {
        LogEntry entry = Info("line");

        Assert.Equal("日志", entry.Kind);
        Assert.Equal(string.Empty, entry.HexColumn);
        Assert.Equal(string.Empty, entry.AsciiColumn);
    }

    private static LogEntry Info(string message, LogLevel level = LogLevel.Information) =>
        new(Start, level, "Test", message, null);

    private static FrameEntry Tx(int length) => new(Start, FrameDirection.Tx, length, "AA", false, null);

    private static FrameEntry Rx(int length) => new(Start + TimeSpan.FromSeconds(1), FrameDirection.Rx, length, "BB", false, null);
}
