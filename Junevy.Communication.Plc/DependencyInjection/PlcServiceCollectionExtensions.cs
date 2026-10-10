using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.DependencyInjection;

/// <summary>地址型协议客户端的依赖注入扩展方法（命名方式与通道族的 <c>ChannelServiceCollectionExtensions</c> 一致）。</summary>
public static class PlcServiceCollectionExtensions
{
    /// <summary>
    /// 以单例注册 <see cref="PlcClientFactory"/>。创建器由协议包的扩展方法注册（例如 AddMelsec）。
    /// 容器中没有 <see cref="ILoggerFactory"/> 时工厂不输出日志。重复调用是幂等的（TryAdd）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddPlcClients(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<PlcClientFactory>(sp => new PlcClientFactory(
            sp.GetServices<IPlcClientCreator>(),
            sp.GetService<ILoggerFactory>()));
        return services;
    }
}
