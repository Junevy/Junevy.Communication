using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.DependencyInjection;

/// <summary>通道族的依赖注入扩展方法（命名空间与 Modbus 的 <c>ModbusServiceCollectionExtensions</c> 一致）。</summary>
public static class ChannelServiceCollectionExtensions
{
    /// <summary>
    /// 以单例注册 <see cref="ChannelFactory"/>。创建器由传输包的扩展方法注册（例如 <c>AddTcpChannels()</c>）。
    /// 容器中没有 <see cref="ILoggerFactory"/> 时工厂不输出日志。重复调用是幂等的（TryAdd）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddChannels(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<ChannelFactory>(sp => new ChannelFactory(
            sp.GetServices<IChannelCreator>(),
            sp.GetService<ILoggerFactory>()));
        return services;
    }
}
