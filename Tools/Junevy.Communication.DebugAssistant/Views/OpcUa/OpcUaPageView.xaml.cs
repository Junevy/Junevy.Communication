using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Junevy.Communication.DebugAssistant.Protocols.OpcUa;

namespace Junevy.Communication.DebugAssistant.Views.OpcUa;

/// <summary>OPC UA 客户端页的视图（计划第 7.8 节）。界面绑定到 <c>OpcUaSessionViewModel</c>。</summary>
public partial class OpcUaPageView : UserControl
{
    public OpcUaPageView()
    {
        InitializeComponent();
    }

    // 双击浏览树的节点：变量加入读写列表。TreeViewItem 的双击事件只在这里转给视图模型，视图模型决定是否接受。
    private void OnBrowseItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OpcUaNodeItem node } && DataContext is OpcUaSessionViewModel viewModel)
        {
            viewModel.AddBrowsedNodeCommand.Execute(node);
            e.Handled = true;
        }
    }
}
