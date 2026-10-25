namespace Junevy.Communication.DebugAssistant.Services;

/// <summary>全局通知条（WPF 实现位于 Views/Platform，显示在主窗口底部）。可以从任何线程调用。</summary>
public interface INotificationService
{
    /// <summary>显示一条通知。</summary>
    void Show(NotificationKind kind, string message);
}

/// <summary>通知的种类，决定通知条的颜色。</summary>
public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}
