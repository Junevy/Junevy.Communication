using System.Windows.Controls;
using System.Windows.Input;
using Junevy.Communication.DebugAssistant.Protocols.Ftp;

namespace Junevy.Communication.DebugAssistant.Views.Ftp;

/// <summary>FTP/FTPS 页的视图（计划第 7.12 节）。界面绑定到 <c>FtpSessionViewModel</c>。</summary>
public partial class FtpPageView : UserControl
{
    public FtpPageView()
    {
        InitializeComponent();
    }

    // 双击目录列表的行：进入目录。双击文件时视图模型给出说明，不做任何事。
    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FtpSessionViewModel viewModel && viewModel.SelectedEntry is not null)
        {
            viewModel.EnterDirectoryCommand.Execute(viewModel.SelectedEntry);
        }
    }
}
