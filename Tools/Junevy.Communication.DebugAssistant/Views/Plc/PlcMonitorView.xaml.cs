using System.Windows.Controls;

namespace Junevy.Communication.DebugAssistant.Views.Plc;

/// <summary>PLC 监控表：逐行轮询、写入新值与删除（计划第 7.1 节），放在协议页签的底部活动区。</summary>
public partial class PlcMonitorView : UserControl
{
    public PlcMonitorView()
    {
        InitializeComponent();
    }
}
