using System.Text;
using System.Windows;
using Junevy.Communication.DebugAssistant.Hosting;
using Junevy.Communication.DebugAssistant.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant;

/// <summary>应用的宿主：构建 DI 容器、启动主窗口，退出时释放容器（DA-D9）。</summary>
public partial class App : Application
{
    private IHost? _host;

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

        var window = new Views.Shell.MainWindow
        {
            DataContext = _host.Services.GetRequiredService<MainWindowViewModel>()
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
