using Junevy.Communication.Channels;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp;

/// <summary>
/// <see cref="TcpClientChannelConfig"/> 的通道创建器，由 <see cref="ChannelFactory"/> 按配置的精确类型选择。
/// </summary>
public sealed class TcpClientChannelCreator : IChannelCreator
{
    /// <inheritdoc />
    public Type ConfigType => typeof(TcpClientChannelConfig);

    /// <summary>
    /// 创建 TCP 客户端通道（尚未连接）。配置的校验由 <see cref="TcpClientChannel"/> 的构造函数完成（D5），本方法不修改调用方的配置对象。
    /// </summary>
    /// <param name="name">通道名称。</param>
    /// <param name="config">必须是 <see cref="TcpClientChannelConfig"/>。</param>
    /// <param name="components">
    /// 代码级覆盖。为 <see cref="TcpChannelComponents"/> 时直接使用；为其他 <see cref="ChannelComponents"/> 时复制其各属性；为 null 时只使用配置。
    /// </param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>新建的 <see cref="TcpClientChannel"/>。</returns>
    /// <exception cref="ArgumentNullException">配置或日志工厂为 null。</exception>
    /// <exception cref="ArgumentException">配置不是 <see cref="TcpClientChannelConfig"/>，或配置非法。</exception>
    public IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (loggerFactory == null)
            throw new ArgumentNullException(nameof(loggerFactory));
        if (config is not TcpClientChannelConfig tcpConfig)
            throw new ArgumentException(
                $"TcpClientChannelCreator requires '{typeof(TcpClientChannelConfig).FullName}' but received '{config.GetType().FullName}'.",
                nameof(config));

        return new TcpClientChannel(name, tcpConfig, loggerFactory.CreateLogger<TcpClientChannel>(), ToTcpComponents(components));
    }

    // 普通的 ChannelComponents 复制到新的 TcpChannelComponents（TCP 专属成员保持默认值）；TcpChannelComponents 直接使用。
    private static TcpChannelComponents ToTcpComponents(ChannelComponents? components)
    {
        if (components is TcpChannelComponents tcpComponents)
            return tcpComponents;

        if (components == null)
            return new TcpChannelComponents();

        return new TcpChannelComponents
        {
            FrameCodec = components.FrameCodec,
            Correlation = components.Correlation,
            KeyExtractor = components.KeyExtractor,
            Initializer = components.Initializer,
            HealthProbe = components.HealthProbe,
            ReconnectPolicy = components.ReconnectPolicy,
        };
    }
}
