using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.DebugAssistant.Services;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>页签底部的报文与日志面板：筛选、暂停、清空、导出、统计（计划第 5.3 节）。</summary>
public sealed partial class LogPanelViewModel : ObservableObject
{
    private readonly SessionLog _log;
    private readonly IFileDialogService _files;
    private readonly INotificationService _notifications;

    public LogPanelViewModel(SessionLog log, IFileDialogService files, INotificationService notifications)
    {
        _log = log;
        _files = files;
        _notifications = notifications;
        log.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(Statistics));
            OnPropertyChanged(nameof(StatisticsText));
        };
    }

    /// <summary>可见的条目（绑定到 DataGrid）。</summary>
    public ObservableCollection<SessionEntry> Entries => _log.Visible;

    /// <summary>可选的最低日志级别。</summary>
    public IReadOnlyList<LogLevel> Levels { get; } = [LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error];

    /// <summary>收发统计，每次刷新后更新。</summary>
    public FrameStatistics Statistics => _log.Statistics;

    /// <summary>统计的一行文字：帧数、字节数与最后一次收发的时间。</summary>
    public string StatisticsText
    {
        get
        {
            FrameStatistics stats = _log.Statistics;
            return $"发送 {stats.TxFrames} 帧 / {stats.TxBytes} 字节（最后 {FormatTime(stats.LastTx)}）    " +
                $"接收 {stats.RxFrames} 帧 / {stats.RxBytes} 字节（最后 {FormatTime(stats.LastRx)}）";
        }
    }

    [ObservableProperty]
    private bool _showFrames = true;

    [ObservableProperty]
    private bool _showLogs = true;

    [ObservableProperty]
    private LogLevel _minimumLevel = LogLevel.Debug;

    [ObservableProperty]
    private bool _showHex = true;

    [ObservableProperty]
    private bool _showAscii;

    [ObservableProperty]
    private bool _showTimestamps = true;

    [ObservableProperty]
    private bool _isPaused;

    partial void OnShowFramesChanged(bool value) => ApplyFilter();

    partial void OnShowLogsChanged(bool value) => ApplyFilter();

    partial void OnMinimumLevelChanged(LogLevel value) => ApplyFilter();

    partial void OnIsPausedChanged(bool value) => _log.IsPaused = value;

    [RelayCommand]
    private void Clear() => _log.Clear();

    /// <summary>把当前可见的条目导出为文本文件（DA-D11 之外的导出，计划第 5.3 节）。</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        string suggested = $"debug-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        string? path = _files.PickSaveFile("导出日志", suggested, "文本文件 (*.txt)|*.txt");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, SessionLogText.Format(Entries));
            _notifications.Show(NotificationKind.Success, $"已导出 {Entries.Count} 行到 {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Show(NotificationKind.Error, $"导出失败：{ex.Message}");
        }
    }

    private void ApplyFilter() => _log.Filter = new LogFilter(ShowFrames, ShowLogs, MinimumLevel);

    private static string FormatTime(DateTimeOffset? time) => time is { } value ? value.ToString("HH:mm:ss.fff") : "—";
}
