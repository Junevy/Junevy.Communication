using Junevy.Communication.Channels;
using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Plc;

/// <summary>
/// 协议客户端的传输绑定（P25-D4）：协议按创建器声明的交付方式与介质配置组件，再由创建器创建通道。
/// Plc 不引用任何传输包；具体传输由调用方提供的 <see cref="IChannelCreator"/> 决定。
/// </summary>
public static class PlcChannelBinding
{
    /// <summary>
    /// 按创建器声明的传输特征配置组件，并由创建器创建通道（尚未连接）。
    /// </summary>
    /// <param name="creator">通道创建器。</param>
    /// <param name="name">通道名称；为 null 时使用传输的默认名称。</param>
    /// <param name="transport">传输配置，精确类型必须是 <see cref="IConfigTypedCreator.ConfigType"/>。</param>
    /// <param name="configureComponents">接收创建器的交付方式与介质，返回配置好协议的组件。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时使用 <see cref="NullLoggerFactory"/>。</param>
    /// <returns>新建的客户端通道。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="creator"/>、<paramref name="transport"/> 或 <paramref name="configureComponents"/> 为 null。</exception>
    /// <exception cref="ArgumentException">传输配置的精确类型不是创建器的配置类型；或协议、创建器认为组合或配置非法。</exception>
    public static IClientChannel CreateChannel(
        IChannelCreator creator,
        string? name,
        IChannelConfig transport,
        Func<ChannelDelivery, ChannelMedium, ChannelComponents> configureComponents,
        ILoggerFactory? loggerFactory = null)
    {
        if (creator == null)
            throw new ArgumentNullException(nameof(creator));
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));
        if (configureComponents == null)
            throw new ArgumentNullException(nameof(configureComponents));
        if (transport.GetType() != creator.ConfigType)
            throw new ArgumentException(
                $"The transport configuration type '{transport.GetType().FullName}' does not match the creator's configuration type '{creator.ConfigType.FullName}'.",
                nameof(transport));

        ChannelComponents components = configureComponents(creator.Delivery, creator.Medium);
        return creator.Create(name, transport, components, loggerFactory ?? NullLoggerFactory.Instance);
    }
}
