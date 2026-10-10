using Junevy.Communication.Channels;
using Junevy.Communication.Channels.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Junevy.Communication.Serial.DependencyInjection;

/// <summary>串口通道的依赖注入扩展方法（命名空间与 Tcp 的约定一致）。</summary>
public static class SerialServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="SerialChannelCreator"/> 并调用 <c>AddChannels()</c>，之后可从容器获取 <see cref="ChannelFactory"/>。
    /// 重复调用是幂等的：容器中只有一个串口创建器。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
    public static IServiceCollection AddSerialChannels(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        services.AddChannels();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelCreator, SerialChannelCreator>());
        return services;
    }
}
