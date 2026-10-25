using Junevy.Communication.DebugAssistant.Services;
using Microsoft.Win32;

namespace Junevy.Communication.DebugAssistant.Views.Platform;

/// <summary>基于 WPF 通用对话框的文件选择。</summary>
internal sealed class WpfFileDialogService : IFileDialogService
{
    public string? PickSaveFile(string title, string suggestedFileName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggestedFileName, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
