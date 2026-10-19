using Junevy.Communication.DebugAssistant.Services;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>所有页签共用的运行时依赖：界面派发、时钟、文件对话框与通知。时钟与服务可在测试中替换。</summary>
public sealed record SessionServices(
    IUiDispatcher Dispatcher,
    TimeProvider Clock,
    IFileDialogService Files,
    INotificationService Notifications);
