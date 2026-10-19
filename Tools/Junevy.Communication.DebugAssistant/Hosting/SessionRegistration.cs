using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Hosting;

/// <summary>页签模块的 DI 注册：工厂、命名器、共享依赖，以及每种页签的键控服务（DA-D19）。</summary>
internal static class SessionRegistration
{
    /// <summary>已经实现的页签。其余种类使用占位页签，每个种类完成后在这里加一行。</summary>
    private static readonly Dictionary<ProtocolKind, Func<ProtocolDescriptor, SessionServices, SessionViewModel>> Pages = new()
    {
        [ProtocolKind.TcpClient] = (descriptor, services) => new TcpClientSessionViewModel(descriptor, services),
        [ProtocolKind.Udp] = (descriptor, services) => new UdpSessionViewModel(descriptor, services),
        [ProtocolKind.Serial] = (descriptor, services) => new SerialSessionViewModel(descriptor, services),
    };

    internal static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddSingleton<SessionNamer>();
        services.AddSingleton<ISessionFactory, SessionFactory>();

        foreach (ProtocolDescriptor descriptor in ProtocolCatalog.All)
        {
            Func<ProtocolDescriptor, SessionServices, SessionViewModel> create = Pages.TryGetValue(descriptor.Kind, out var page)
                ? page
                : (placeholder, dependencies) => new PlaceholderSessionViewModel(placeholder, dependencies);
            services.AddKeyedTransient<SessionViewModel>(
                descriptor.Kind,
                (provider, _) => create(descriptor, provider.GetRequiredService<SessionServices>()));
        }

        return services;
    }
}
