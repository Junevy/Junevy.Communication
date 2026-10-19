using System.Windows.Threading;
using Junevy.Communication.DebugAssistant.Services;

namespace Junevy.Communication.DebugAssistant.Views.Platform;

/// <summary>把工作派发到创建本对象的界面线程（应用启动时在界面线程上解析）。</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action);
}
