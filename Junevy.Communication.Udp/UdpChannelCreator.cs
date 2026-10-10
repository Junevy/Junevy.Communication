using Junevy.Communication.Channels;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Udp;

/// <summary>
/// <see cref="UdpChannelConfig"/> 的通道创建器，由 <see cref="ChannelFactory"/> 按配置的精确类型选择。
/// </summary>
public sealed class UdpChannelCreator : IChannelCreator
{
    /// <inheritdoc />
    public Type ConfigType => typeof(UdpChannelConfig);

    /// <summary>
    /// 创建 UDP 通道（尚未连接）。配置的校验由 <see cref="UdpChannel"/> 的构造函数完成（D5），本方法不修改调用方的配置对象。
    /// </summary>
    /// <param name="name">通道名称。</param>
    /// <param name="config">必须是 <see cref="UdpChannelConfig"/>。</param>
    /// <param name="components">代码级覆盖；为 null 时只使用配置。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>新建的 <see cref="UdpChannel"/>。</returns>
    /// <exception cref="ArgumentNullException">配置、名称或日志工厂为 null。</exception>
    /// <exception cref="ArgumentException">配置不是 <see cref="UdpChannelConfig"/>，或配置非法。</exception>
    public IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (loggerFactory == null)
            throw new ArgumentNullException(nameof(loggerFactory));
        if (config is not UdpChannelConfig udpConfig)
            throw new ArgumentException(
                $"UdpChannelCreator requires '{typeof(UdpChannelConfig).FullName}' but received '{config.GetType().FullName}'.",
                nameof(config));

        return new UdpChannel(name, udpConfig, loggerFactory.CreateLogger<UdpChannel>(), components);
    }
}
