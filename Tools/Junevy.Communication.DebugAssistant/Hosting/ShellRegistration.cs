using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Views.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Hosting;

/// <summary>外壳模块的 DI 注册。</summary>
public static class ShellRegistration
{
    public static IServiceCollection AddShellServices(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<IConfirmService, WpfConfirmService>();
        services.AddSingleton<IFileDialogService, WpfFileDialogService>();
        services.AddSingleton<INotificationService, WpfNotificationService>();
        services.AddSingleton(provider => new SessionServices(
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IFileDialogService>(),
            provider.GetRequiredService<INotificationService>()));
        services.AddSessionServices();
        services.AddTransient<MainWindowViewModel>();
        return services;
    }
}
