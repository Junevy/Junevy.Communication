using System.ComponentModel;
using System.Text;
using System.Windows;
using Junevy.Communication.DebugAssistant.Hosting;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Controls.Controls.Bar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant;

/// <summary>应用的宿主：构建 DI 容器、启动主窗口，退出时释放容器（DA-D9）。</summary>
public partial class App : Application
{
    private IHost? _host;
    private bool _shuttingDown;
    private bool _closeAllowed;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // GBK 等代码页编码（Ftp 的文件名、串口文本）需要显式注册（.NET 8 不内置）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddShellServices();
        _host = builder.Build();

        var viewModel = _host.Services.GetRequiredService<MainWindowViewModel>();
        var window = new Views.Shell.MainWindow { DataContext = viewModel };
        // 先注册通知宿主：恢复工作区时可能要说明被跳过的页签（MessageBarService 没有宿主时会抛出异常）。
        MessageBarService.SetPresenter((MessageBarPresenter)window.FindName("NotificationPresenter"));
        viewModel.RestoreWorkspace();
        window.Closing += OnMainWindowClosing;
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 退出时先取消关闭，并发释放全部会话（最长 10 秒，DA-D9），完成后再真正退出。
    /// 释放是异步的，而 <see cref="Window.Closing"/> 是同步事件，因此只能在事件处理器里等待。
    /// </summary>
    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed)
        {
            return;
        }

        e.Cancel = true;
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        if (sender is Window { DataContext: MainWindowViewModel viewModel })
        {
            await viewModel.ShutdownAsync();
        }

        _closeAllowed = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
