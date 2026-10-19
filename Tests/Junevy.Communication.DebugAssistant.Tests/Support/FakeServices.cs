using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>记录通知并按设定返回保存路径的文件对话框。</summary>
internal sealed class FakeFileDialogService : IFileDialogService
{
    /// <summary>保存对话框返回的路径；null 表示用户取消。</summary>
    public string? SaveTo { get; set; }

    /// <summary>打开对话框返回的路径；null 表示用户取消。</summary>
    public string? OpenFrom { get; set; }

    public string? PickSaveFile(string title, string suggestedFileName, string filter) => SaveTo;

    public string? PickOpenFile(string title, string filter) => OpenFrom;
}

/// <summary>把通知记录下来，便于断言。</summary>
internal sealed class RecordingNotificationService : INotificationService
{
    public List<(NotificationKind Kind, string Message)> Shown { get; } = [];

    public void Show(NotificationKind kind, string message) => Shown.Add((kind, message));
}

/// <summary>页签依赖的测试组合：界面派发、假时钟、假对话框与记录通知。</summary>
internal static class FakeServices
{
    public static SessionServices Create(ManualUiDispatcher? dispatcher = null, FakeFileDialogService? files = null) => new(
        dispatcher ?? new ManualUiDispatcher(),
        new FakeClock(),
        files ?? new FakeFileDialogService(),
        new RecordingNotificationService());
}
