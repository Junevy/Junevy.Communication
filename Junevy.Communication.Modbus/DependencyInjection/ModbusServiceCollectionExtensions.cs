using Junevy.Communication.Plc;
using Junevy.Communication.Plc.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Junevy.Communication.Modbus.DependencyInjection;

/// <summary>Modbus 的依赖注入扩展（P7-D14）。</summary>
public static class ModbusServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Modbus 的创建器，并注册套件的客户端工厂（<see cref="PlcClientFactory"/>）。可以重复调用，与 <c>AddMelsec</c> 共存。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddModbus(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlcClientCreator, ModbusClientCreator>());
        return services.AddPlcClients();
    }
}
