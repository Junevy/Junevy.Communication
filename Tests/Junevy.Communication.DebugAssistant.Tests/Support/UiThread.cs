using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Junevy.Communication.DebugAssistant;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 运行需要 WPF 对象的测试。<see cref="Application"/> 与控件只属于创建它们的线程，因此全部 UI 测试共用
/// 一条 STA 线程（它的 <see cref="Dispatcher"/>），测试线程通过同步的 <c>Invoke</c> 把工作交给它。
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(StartApplicationThread);

    internal static T Invoke<T>(Func<T> function) => Instance.Value.Invoke(function);

    internal static void Invoke(Action action) => Instance.Value.Invoke(action);

    /// <summary>在界面线程上运行异步流程：其中的 await 回到界面线程（与应用相同），视图与视图模型的集合只被一个线程访问。</summary>
    internal static Task RunAsync(Func<Task> body) => Instance.Value.InvokeAsync(body).Task.Unwrap();

    private static Dispatcher StartApplicationThread()
    {
        Dispatcher? dispatcher = null;
        using var started = new ManualResetEventSlim(initialState: false);
        var thread = new Thread(() =>
        {
            _ = new App();
            dispatcher = Dispatcher.CurrentDispatcher;
            started.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "DebugAssistant WPF test thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        started.Wait();
        return dispatcher!;
    }
}
