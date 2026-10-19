using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Hosting;

/// <summary>页签模块的 DI 注册：工厂、命名器、共享依赖，以及每种页签的键控服务（DA-D19）。</summary>
internal static class SessionRegistration
{
    internal static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddSingleton<SessionNamer>();
        services.AddSingleton<ISessionFactory, SessionFactory>();

        // 尚未实现的种类使用占位页签；每个种类的真实页签完成后改为 AddKeyedTransient<SessionViewModel, T>(kind)。
        foreach (ProtocolDescriptor descriptor in ProtocolCatalog.All)
        {
            services.AddKeyedTransient<SessionViewModel>(
                descriptor.Kind,
                (provider, _) => new PlaceholderSessionViewModel(descriptor, provider.GetRequiredService<SessionServices>()));
        }

        return services;
    }
}
