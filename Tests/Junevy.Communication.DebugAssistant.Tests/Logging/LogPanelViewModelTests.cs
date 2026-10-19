using System.IO;
using Junevy.Communication.DebugAssistant.Logging;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Logging;

public sealed class LogPanelViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ShowFramesOff_HidesFramesFromTheVisibleEntries()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        session.Log.Append(new FrameEntry(Start, FrameDirection.Tx, 2, "AA-BB", false, null));
        session.Log.Append(new LogEntry(Start, LogLevel.Information, "T", "line", null));
        session.Log.Flush();

        session.LogPanel.ShowFrames = false;

        Assert.Equal(new[] { "日志" }, session.LogPanel.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task Pause_HoldsTheEntries_UntilResumed()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        session.LogPanel.IsPaused = true;
        session.Log.Append(new LogEntry(Start, LogLevel.Information, "T", "held", null));

        session.Log.Flush();
        Assert.Empty(session.LogPanel.Entries);

        session.LogPanel.IsPaused = false;
        session.Log.Flush();
        Assert.Single(session.LogPanel.Entries);
    }

    [Fact]
    public async Task ClearCommand_EmptiesTheEntries()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        session.Log.Append(new LogEntry(Start, LogLevel.Information, "T", "line", null));
        session.Log.Flush();

        session.LogPanel.ClearCommand.Execute(null);

        Assert.Empty(session.LogPanel.Entries);
    }

    [Fact]
    public async Task StatisticsText_ReflectsTheFrameCounts()
    {
        await using SessionViewModel session = TestShell.CreateSession();
        session.Log.Append(new FrameEntry(Start, FrameDirection.Tx, 3, "AA-BB-CC", false, null));
        session.Log.Flush();

        Assert.Contains("发送 1 帧 / 3 字节", session.LogPanel.StatisticsText, StringComparison.Ordinal);
        Assert.Contains("接收 0 帧 / 0 字节", session.LogPanel.StatisticsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportCommand_WritesTheVisibleEntries_ToTheChosenFile()
    {
        var (services, _) = TestShell.Create();
        var files = (FakeFileDialogService)services.GetRequiredService<IFileDialogService>();
        var notifications = (RecordingNotificationService)services.GetRequiredService<INotificationService>();
        await using SessionViewModel session = services.GetRequiredKeyedService<SessionViewModel>(ProtocolKind.Modbus);
        session.Log.Append(new FrameEntry(Start, FrameDirection.Tx, 3, "AA-BB-CC", false, null));
        session.Log.Flush();
        string path = Path.Combine(Path.GetTempPath(), $"debug-assistant-export-{Guid.NewGuid():N}.txt");
        files.SaveTo = path;
        try
        {
            await session.LogPanel.ExportCommand.ExecuteAsync(null);

            string text = await File.ReadAllTextAsync(path);
            Assert.Contains("发送 3 字节: AA-BB-CC", text, StringComparison.Ordinal);
            Assert.Equal(NotificationKind.Success, Assert.Single(notifications.Shown).Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportCommand_WhenTheDialogIsCancelled_WritesNothing()
    {
        var (services, _) = TestShell.Create();
        var notifications = (RecordingNotificationService)services.GetRequiredService<INotificationService>();
        await using SessionViewModel session = services.GetRequiredKeyedService<SessionViewModel>(ProtocolKind.Modbus);

        await session.LogPanel.ExportCommand.ExecuteAsync(null);

        Assert.Empty(notifications.Shown);
    }
}
