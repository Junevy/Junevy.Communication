using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Channels;

/// <summary>
/// 客户端通道工厂：按名称创建、获取与移除客户端通道（<see cref="IClientChannel"/>）。创建时按配置的精确运行时类型选择
/// <see cref="IChannelCreator"/>。工厂只管理客户端通道（D17）；TCP 服务端由宿主直接创建和持有。
/// </summary>
/// <remarks>
/// 名称、别名与释放由 <see cref="NamedFactory{TItem}"/> 提供（底层为 <see cref="NamedRegistry{T}"/>）：同名获取返回同一实例，别名与其目标共享实例，移除或释放时释放通道。
/// 配置非法时由创建器抛出 <see cref="ArgumentException"/>，调用方的配置对象不会被修改（D5）。
/// 工厂释放之后，除 <see cref="NamedFactory{TItem}.Dispose()"/> 与 <see cref="NamedFactory{TItem}.DisposeAsync"/> 外的成员都抛出 <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class ChannelFactory : NamedFactory<IClientChannel>
{
    private readonly CreatorCatalog<IChannelCreator> creatorCatalog;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>
    /// 创建通道工厂。
    /// </summary>
    /// <param name="creators">通道创建器；每种配置类型只能注册一个创建器。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="creators"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="creators"/> 含有 null，或同一配置类型注册了多个创建器。</exception>
    public ChannelFactory(IEnumerable<IChannelCreator> creators, ILoggerFactory? loggerFactory = null)
        : base((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ChannelFactory>())
    {
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        creatorCatalog = new CreatorCatalog<IChannelCreator>(creators);
    }

    /// <summary>
    /// 按名称获取通道；名称不存在时按 <paramref name="config"/> 的精确类型选择创建器，创建并注册。
    /// 名称已存在时直接返回已有实例，<paramref name="config"/> 与 <paramref name="components"/> 不生效。
    /// </summary>
    /// <param name="name">通道名称或别名；不能为 null 或空。</param>
    /// <param name="config">通道配置；其精确运行时类型必须有对应的创建器。</param>
    /// <param name="components">代码级覆盖；为 null 时只使用配置。</param>
    /// <returns>已注册（或刚创建）的通道。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空，或配置非法。</exception>
    /// <exception cref="NotSupportedException">没有与配置的精确类型对应的创建器。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public IClientChannel GetOrAdd(string name, IChannelConfig config, ChannelComponents? components = null)
    {
        ThrowIfDisposed();
        IChannelCreator creator = creatorCatalog.Resolve(config);
        return GetOrAddCore(name, key => creator.Create(key, config, components, loggerFactory));
    }

    /// <summary>
    /// 按名称创建并注册通道。名称已存在时不创建，返回 false。
    /// </summary>
    /// <param name="name">通道名称；不能为 null 或空。</param>
    /// <param name="config">通道配置；其精确运行时类型必须有对应的创建器。</param>
    /// <param name="channel">注册成功时为新建的通道；否则为 null。</param>
    /// <param name="components">代码级覆盖；为 null 时只使用配置。</param>
    /// <returns>注册成功返回 true；名称已存在返回 false。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空，或配置非法。</exception>
    /// <exception cref="NotSupportedException">没有与配置的精确类型对应的创建器。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool TryAdd(string name, IChannelConfig config, out IClientChannel? channel, ChannelComponents? components = null)
    {
        ThrowIfDisposed();
        IChannelCreator creator = creatorCatalog.Resolve(config);
        return TryAddCore(name, key => creator.Create(key, config, components, loggerFactory), out channel);
    }
}
