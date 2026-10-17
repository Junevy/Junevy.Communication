using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.DependencyInjection;

/// <summary>HTTP 服务端的依赖注入扩展方法（命名空间约定与 <c>AddWebApiClients</c> 一致）。</summary>
public static class WebApiServerServiceCollectionExtensions
{
    /// <summary>
    /// 以单例注册 <see cref="WebApiServerFactory"/>。容器中没有 <see cref="ILoggerFactory"/> 时工厂不输出日志。
    /// 重复调用是幂等的（TryAdd）；与 <c>AddWebApiClients</c> 互不影响。容器释放时工厂随之释放，它创建的服务器也被释放。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddWebApiServers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<WebApiServerFactory>(sp => new WebApiServerFactory(sp.GetService<ILoggerFactory>()));
        return services;
    }
}
