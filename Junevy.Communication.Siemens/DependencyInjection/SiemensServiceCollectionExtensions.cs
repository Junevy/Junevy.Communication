using Junevy.Communication.Plc;
using Junevy.Communication.Plc.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Junevy.Communication.Siemens.DependencyInjection;

/// <summary>Siemens 客户端的依赖注入扩展方法（命名方式与 <see cref="PlcServiceCollectionExtensions"/> 一致）。</summary>
public static class SiemensServiceCollectionExtensions
{
    /// <summary>
    /// 注册 S7 的客户端创建器（<see cref="S7ClientCreator"/>），并调用 <see cref="PlcServiceCollectionExtensions.AddPlcClients"/>。
    /// 重复调用是幂等的。之后经 <see cref="PlcClientFactory"/> 以 <see cref="S7ClientConfig"/> 获取客户端。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddSiemensS7(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlcClientCreator, S7ClientCreator>());
        return services.AddPlcClients();
    }
}
