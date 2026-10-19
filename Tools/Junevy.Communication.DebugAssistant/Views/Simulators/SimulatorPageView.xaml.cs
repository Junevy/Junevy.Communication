using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Junevy.Communication.DebugAssistant.Protocols.Simulators;

namespace Junevy.Communication.DebugAssistant.Views.Simulators;

/// <summary>模拟设备页签：宿主、设备、内存表、故障注入与请求日志；内存表在页面可见时每 0.5 秒刷新一次。</summary>
public partial class SimulatorPageView : UserControl
{
    private readonly DispatcherTimer _memoryRefresh = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public SimulatorPageView()
    {
        InitializeComponent();
        _memoryRefresh.Tick += (_, _) => (DataContext as SimulatorSessionViewModel)?.RefreshMemoryCommand.Execute(null);
        Loaded += (_, _) => _memoryRefresh.Start();
        Unloaded += (_, _) => _memoryRefresh.Stop();
    }
}
