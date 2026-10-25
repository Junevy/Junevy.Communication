using Junevy.Communication.DebugAssistant.Services;
using Junevy.Controls.Common;
using Junevy.Controls.Controls.Dialog;

namespace Junevy.Communication.DebugAssistant.Views.Platform;

/// <summary>基于 Junevy.Controls 的确认对话框实现 <see cref="IConfirmService"/>。</summary>
internal sealed class WpfConfirmService : IConfirmService
{
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        ConfirmDialogResult result = await ConfirmDialogService.ShowAsync(title, message, ConfirmDialogButtons.OkCancel);
        return result == ConfirmDialogResult.Confirm;
    }
}
