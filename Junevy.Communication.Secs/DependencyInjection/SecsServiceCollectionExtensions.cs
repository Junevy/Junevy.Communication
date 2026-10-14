using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.DependencyInjection;

/// <summary>设备端的依赖注入扩展方法（命名空间约定与 <c>AddChannels</c>、<c>AddPlcClients</c> 一致）。</summary>
public static class SecsServiceCollectionExtensions
{
    /// <summary>
    /// 以单例注册 <see cref="HsmsEquipmentFactory"/>。容器中没有 <see cref="ILoggerFactory"/> 时设备端不输出日志。
    /// 重复调用是幂等的（TryAdd）；容器释放时工厂随之释放，它创建的设备端也被释放。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddSecs(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<HsmsEquipmentFactory>(sp => new HsmsEquipmentFactory(sp.GetService<ILoggerFactory>()));
        return services;
    }
}