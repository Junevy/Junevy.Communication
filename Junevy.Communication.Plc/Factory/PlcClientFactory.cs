using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Plc;

/// <summary>
/// 协议客户端工厂：按名称创建、获取与移除地址型 PLC 客户端。创建时按配置的精确运行时类型选择 <see cref="IPlcClientCreator"/>。
/// 行为与通道族的 <c>ChannelFactory</c> 一致。
/// </summary>
/// <remarks>
/// 名称、别名与释放由 <see cref="NamedFactory{TItem}"/> 提供（底层为 <see cref="NamedRegistry{T}"/>）：同名获取返回同一实例，别名与其目标共享实例，移除或释放时释放客户端。
/// 配置非法时由创建器抛出 <see cref="ArgumentException"/>。工厂释放之后，除 <see cref="NamedFactory{TItem}.Dispose()"/> 与 <see cref="NamedFactory{TItem}.DisposeAsync"/> 外的成员都抛出 <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class PlcClientFactory : NamedFactory<IPlcClient>
{
    private readonly CreatorCatalog<IPlcClientCreator> creatorCatalog;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>
    /// 创建客户端工厂。
    /// </summary>
    /// <param name="creators">客户端创建器；每种配置类型只能注册一个。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="creators"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="creators"/> 含有 null，或同一配置类型注册了多个创建器。</exception>
    public PlcClientFactory(IEnumerable<IPlcClientCreator> creators, ILoggerFactory? loggerFactory = null)
        : base((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PlcClientFactory>())
    {
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        creatorCatalog = new CreatorCatalog<IPlcClientCreator>(creators);
    }

    /// <summary>
    /// 按名称获取客户端；名称不存在时按 <paramref name="config"/> 的精确类型选择创建器，创建并注册。
    /// 名称已存在时直接返回已有实例，<paramref name="config"/> 不生效。
    /// </summary>
    /// <param name="name">客户端名称或别名；不能为 null 或空。</param>
    /// <param name="config">客户端配置；其精确运行时类型必须有对应的创建器。</param>
    /// <returns>已注册（或刚创建）的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空，或配置非法。</exception>
    /// <exception cref="NotSupportedException">没有与配置的精确类型对应的创建器。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public IPlcClient GetOrAdd(string name, IPlcClientConfig config)
    {
        ThrowIfDisposed();
        IPlcClientCreator creator = creatorCatalog.Resolve(config);
        return GetOrAddCore(name, key => creator.Create(key, config, loggerFactory));
    }
}
