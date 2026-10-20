using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Views.Platform;
using Junevy.Communication.DebugAssistant.Workspace;
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
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ITransportCreatorProvider>(),
            provider.GetRequiredService<IConfirmService>()));
        services.AddSingleton<ITransportCreatorProvider, TransportCreatorProvider>();
        services.AddSessionServices();
        services.AddSingleton(new WorkspaceStore(WorkspaceStore.DefaultPath));
        services.AddSingleton<WorkspaceService>();
        services.AddTransient<MainWindowViewModel>();
        return services;
    }
}
