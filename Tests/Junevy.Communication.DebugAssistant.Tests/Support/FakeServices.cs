using Junevy.Communication.DebugAssistant.Protocols.Plc;
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

/// <summary>
/// 记录确认请求并按设定回答的确认服务（计划 Task 19：递归删除需要确认，用测试替身确认）。
/// </summary>
internal sealed class FakeConfirmService : IConfirmService
{
    /// <summary>用户的回答；默认确认。</summary>
    public bool Answer { get; set; } = true;

    /// <summary>被询问的消息，按顺序。</summary>
    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Asked.Add(message);
        return Task.FromResult(Answer);
    }
}

/// <summary>页签依赖的测试组合：界面派发、假时钟、假对话框、记录通知与假确认。</summary>
internal static class FakeServices
{
    public static SessionServices Create(
        ManualUiDispatcher? dispatcher = null,
        FakeFileDialogService? files = null,
        ITransportCreatorProvider? transports = null,
        FakeConfirmService? confirm = null) => new(
        dispatcher ?? new ManualUiDispatcher(),
        new FakeClock(),
        files ?? new FakeFileDialogService(),
        new RecordingNotificationService(),
        transports ?? new TransportCreatorProvider(),
        confirm ?? new FakeConfirmService());
}
