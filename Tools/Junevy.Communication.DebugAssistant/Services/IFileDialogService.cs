namespace Junevy.Communication.DebugAssistant.Services;

/// <summary>文件选择对话框（WPF 实现位于 Views/Platform）。取消时返回 null。</summary>
public interface IFileDialogService
{
    /// <summary>让用户选择要保存的文件。</summary>
    string? PickSaveFile(string title, string suggestedFileName, string filter);

    /// <summary>让用户选择要打开的文件。</summary>
    string? PickOpenFile(string title, string filter);
}
