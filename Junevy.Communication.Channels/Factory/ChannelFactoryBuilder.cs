using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels;

/// <summary>
/// 无 DI 容器的宿主（例如 Prism）使用的 <see cref="ChannelFactory"/> 构建器，用法与 <c>ModbusFactoryBuilder</c> 一致。
/// 通道族本身不内置任何创建器：传输包的创建器（例如 <c>TcpClientChannelCreator</c>）由宿主通过 <see cref="WithCreator"/> 注册。
/// </summary>
public sealed class ChannelFactoryBuilder
{
    private readonly List<IChannelCreator> creators = new List<IChannelCreator>();
    private ILoggerFactory? loggerFactory;

    private ChannelFactoryBuilder()
    {
    }

    /// <summary>创建新的构建器。</summary>
    /// <returns>新的构建器实例。</returns>
    public static ChannelFactoryBuilder Create() => new ChannelFactoryBuilder();

    /// <summary>设置日志工厂；未设置时工厂不输出日志。</summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>当前构建器，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> 为 null。</exception>
    public ChannelFactoryBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>
    /// 注册通道创建器。同一配置类型只能注册一个创建器；重复注册会在 <see cref="Build"/> 时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="creator">通道创建器。</param>
    /// <returns>当前构建器，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="creator"/> 为 null。</exception>
    public ChannelFactoryBuilder WithCreator(IChannelCreator creator)
    {
        creators.Add(creator ?? throw new ArgumentNullException(nameof(creator)));
        return this;
    }

    /// <summary>构建通道工厂。每次调用返回新的独立实例。</summary>
    /// <returns>新的通道工厂。</returns>
    /// <exception cref="ArgumentException">同一配置类型注册了多个创建器。</exception>
    public ChannelFactory Build() => new ChannelFactory(creators, loggerFactory);
}
