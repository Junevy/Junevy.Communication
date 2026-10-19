using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.DebugAssistant.Shell;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>调试页签的视图模型基类。子类负责参数与库配置的映射、连接与断开，以及协议特有的命令。</summary>
public abstract partial class SessionViewModel : ObservableObject, IAsyncDisposable
{
    protected SessionViewModel(ProtocolDescriptor descriptor)
    {
        Descriptor = descriptor;
        Title = descriptor.ShortName;
    }

    public ProtocolDescriptor Descriptor { get; }

    public ProtocolKind Kind => Descriptor.Kind;

    /// <summary>页签标题，可以在界面上改名（计划 DA-D8）。</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>释放会话：断开、停止服务端或模拟器。关闭页签与退出程序时调用。</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
