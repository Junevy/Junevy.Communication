using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Ftp.DependencyInjection;

/// <summary>FTP 客户端注册的扩展方法；命名与调用习惯与 <c>AddChannels</c>、<c>AddPlcClients</c>、<c>AddMqttClients</c>、<c>AddOpcUaClients</c> 一致。</summary>
public static class FtpServiceCollectionExtensions
{
    /// <summary>
    /// 容器里注册 <see cref="FtpClientFactory"/>；容器没有 <see cref="ILoggerFactory"/> 时创建的客户端不输出日志。
    /// 可以重复调用：内部用 <c>TryAdd</c>，重复注册不会覆盖，也不会因此产生多个工厂。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddFtpClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<FtpClientFactory>(sp => new FtpClientFactory(sp.GetService<ILoggerFactory>()));
        return services;
    }
}
