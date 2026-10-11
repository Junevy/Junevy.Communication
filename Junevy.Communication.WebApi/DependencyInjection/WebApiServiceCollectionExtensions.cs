using Junevy.Communication.WebApi.Factory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.DependencyInjection;

/// <summary>WebApi 客户端的依赖注入扩展方法（命名空间约定与 <c>AddChannels</c>、<c>AddPlcClients</c> 一致）。</summary>
public static class WebApiServiceCollectionExtensions
{
    /// <summary>
    /// 以单例注册 <see cref="WebApiClientFactory"/>。容器中没有 <see cref="ILoggerFactory"/> 时工厂不输出日志。
    /// 重复调用是幂等的（TryAdd）。容器释放时工厂随之释放，它创建的客户端也被释放。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddWebApiClients(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<WebApiClientFactory>(sp => new WebApiClientFactory(sp.GetService<ILoggerFactory>()));
        return services;
    }
}
