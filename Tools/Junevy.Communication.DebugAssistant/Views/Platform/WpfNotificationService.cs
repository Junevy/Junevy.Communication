using Junevy.Communication.DebugAssistant.Services;
using Junevy.Controls.Common;
using Junevy.Controls.Controls.Bar;

namespace Junevy.Communication.DebugAssistant.Views.Platform;

/// <summary>基于 Junevy.Controls 的 <see cref="MessageBarService"/> 实现通知。宿主在 App 启动时注册。</summary>
internal sealed class WpfNotificationService : INotificationService
{
    public void Show(NotificationKind kind, string message) => MessageBarService.Show(ToAppearance(kind), message);

    private static MessageBarAppearance ToAppearance(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => MessageBarAppearance.Success,
        NotificationKind.Warning => MessageBarAppearance.Warning,
        NotificationKind.Error => MessageBarAppearance.Danger,
        _ => MessageBarAppearance.Informational,
    };
}
