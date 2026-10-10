using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc;

/// <summary>
/// 无 DI 容器的宿主使用的 <see cref="PlcClientFactory"/> 构建器，用法与 <c>ChannelFactoryBuilder</c> 一致。
/// 协议包的创建器（例如 MELSEC 的创建器）由宿主通过 <see cref="WithCreator"/> 注册。
/// </summary>
public sealed class PlcClientFactoryBuilder
{
    private readonly List<IPlcClientCreator> creators = new List<IPlcClientCreator>();
    private ILoggerFactory? loggerFactory;

    private PlcClientFactoryBuilder()
    {
    }

    /// <summary>创建新的构建器。</summary>
    /// <returns>新的构建器实例。</returns>
    public static PlcClientFactoryBuilder Create() => new PlcClientFactoryBuilder();

    /// <summary>设置日志工厂；未设置时工厂不输出日志。</summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>当前构建器，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> 为 null。</exception>
    public PlcClientFactoryBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>
    /// 注册客户端创建器。同一配置类型只能注册一个创建器；重复注册会在 <see cref="Build"/> 时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="creator">客户端创建器。</param>
    /// <returns>当前构建器，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="creator"/> 为 null。</exception>
    public PlcClientFactoryBuilder WithCreator(IPlcClientCreator creator)
    {
        creators.Add(creator ?? throw new ArgumentNullException(nameof(creator)));
        return this;
    }

    /// <summary>构建客户端工厂。每次调用返回新的独立实例。</summary>
    /// <returns>新的客户端工厂。</returns>
    /// <exception cref="ArgumentException">同一配置类型注册了多个创建器。</exception>
    public PlcClientFactory Build() => new PlcClientFactory(creators, loggerFactory);
}
