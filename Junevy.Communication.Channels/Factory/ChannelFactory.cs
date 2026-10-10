using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Channels;

/// <summary>
/// 客户端通道工厂：按名称创建、获取与移除客户端通道（<see cref="IClientChannel"/>）。创建时按配置的精确运行时类型选择
/// <see cref="IChannelCreator"/>。工厂只管理客户端通道（D17）；TCP 服务端由宿主直接创建和持有。
/// </summary>
/// <remarks>
/// 名称与别名由 <see cref="NamedRegistry{T}"/> 管理：同名获取返回同一实例，别名与其目标共享实例，移除或释放时释放通道。
/// 配置非法时由创建器抛出 <see cref="ArgumentException"/>，调用方的配置对象不会被修改（D5）。
/// 工厂释放之后，除 <see cref="Dispose()"/> 与 <see cref="DisposeAsync"/> 外的成员都抛出 <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class ChannelFactory : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<Type, IChannelCreator> creatorsByConfigType = new Dictionary<Type, IChannelCreator>();
    private readonly ILoggerFactory loggerFactory;
    private readonly NamedRegistry<IClientChannel> registry;
    private volatile bool disposed;

    /// <summary>
    /// 创建通道工厂。
    /// </summary>
    /// <param name="creators">通道创建器；每种配置类型只能注册一个创建器。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="creators"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="creators"/> 含有 null，或同一配置类型注册了多个创建器。</exception>
    public ChannelFactory(IEnumerable<IChannelCreator> creators, ILoggerFactory? loggerFactory = null)
    {
        if (creators == null)
            throw new ArgumentNullException(nameof(creators));

        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        foreach (IChannelCreator creator in creators)
        {
            if (creator == null)
                throw new ArgumentException("The creator collection must not contain null.", nameof(creators));

            Type configType = creator.ConfigType;
            if (creatorsByConfigType.ContainsKey(configType))
                throw new ArgumentException(
                    $"More than one IChannelCreator is registered for configuration type '{configType.FullName}'.", nameof(creators));

            creatorsByConfigType.Add(configType, creator);
        }

        registry = new NamedRegistry<IClientChannel>(this.loggerFactory.CreateLogger<ChannelFactory>());
    }

    /// <summary>所有名称（含别名）的快照。</summary>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public IEnumerable<string> Keys
    {
        get
        {
            ThrowIfDisposed();
            return registry.Keys;
        }
    }

    /// <summary>直接注册的通道数（不含别名）。</summary>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return registry.Count;
        }
    }

    // ————— 创建与查找 —————

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
        IChannelCreator creator = ResolveCreator(config);
        ValidateName(name);

        return registry.GetOrAdd(name, key => creator.Create(key, config, components, loggerFactory));
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
        channel = null;
        IChannelCreator creator = ResolveCreator(config);
        ValidateName(name);
        if (registry.TryGet(name, out _))
            return false;

        IClientChannel created = creator.Create(name, config, components, loggerFactory);
        bool added = false;
        try
        {
            added = registry.TryAdd(name, created);
        }
        finally
        {
            // 未注册的实例不属于注册表：名称在创建期间被并发占用，或注册表已释放时，由本方法释放。
            if (!added)
                created.Dispose();
        }

        if (!added)
            return false;

        channel = created;
        return true;
    }

    /// <summary>按名称获取已注册的通道；别名会解析到目标通道。</summary>
    /// <param name="name">通道名称或别名。</param>
    /// <param name="channel">找到时为通道；否则为 null。</param>
    /// <returns>找到返回 true。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool TryGet(string name, [NotNullWhen(true)] out IClientChannel? channel)
    {
        ThrowIfDisposed();
        return registry.TryGet(name, out channel);
    }

    /// <summary>按名称获取指定类型的通道。</summary>
    /// <typeparam name="T">期望的通道类型。</typeparam>
    /// <param name="name">通道名称或别名。</param>
    /// <returns>类型匹配的通道。</returns>
    /// <exception cref="KeyNotFoundException">名称未注册，或别名无法解析。</exception>
    /// <exception cref="InvalidCastException">通道的类型与 <typeparamref name="T"/> 不符。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public T GetRequired<T>(string name) where T : class, IClientChannel
    {
        ThrowIfDisposed();
        return registry.GetRequired<T>(name);
    }

    // ————— 移除与别名 —————

    /// <summary>
    /// 移除名称对应的通道并释放它。指向该通道的别名一并移除；同一通道仍被其他直接名称引用时不释放。
    /// </summary>
    /// <param name="name">通道名称或别名。</param>
    /// <returns>条目存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool TryRemove(string name)
    {
        ThrowIfDisposed();
        return registry.TryRemove(name);
    }

    /// <summary>注册别名：<paramref name="aliasName"/> 与 <paramref name="existingName"/> 共享同一个通道。</summary>
    /// <param name="aliasName">别名；不能为 null 或空。</param>
    /// <param name="existingName">目标名称（直接名称或别名）；不能为 null 或空。</param>
    /// <returns>注册成功返回 true；目标不存在或别名已占用时返回 false。</returns>
    /// <exception cref="ArgumentException"><paramref name="aliasName"/> 或 <paramref name="existingName"/> 为 null 或空。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool RegisterAlias(string aliasName, string existingName)
    {
        ThrowIfDisposed();
        return registry.RegisterAlias(aliasName, existingName);
    }

    /// <summary>移除别名。只删除别名本身，不影响目标通道；对直接名称返回 false（直接名称请使用 <see cref="TryRemove"/>）。</summary>
    /// <param name="aliasName">要移除的别名。</param>
    /// <returns>别名存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool RemoveAlias(string aliasName)
    {
        ThrowIfDisposed();
        return registry.RemoveAlias(aliasName);
    }

    // ————— 释放 —————

    /// <summary>
    /// 释放工厂及其全部通道。同步等待每个通道的释放完成（见 <see cref="NamedRegistry{T}.Dispose"/>）。可重复调用。
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        registry.Dispose();
    }

    /// <summary>异步释放工厂及其全部通道。可重复调用。</summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync()
    {
        disposed = true;
        return registry.DisposeAsync();
    }

    // ————— 辅助 —————

    // 按配置的精确运行时类型选择创建器；不沿基类链查找，子类型配置需要自己的创建器。
    private IChannelCreator ResolveCreator(IChannelConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        Type configType = config.GetType();
        if (creatorsByConfigType.TryGetValue(configType, out var creator))
            return creator;

        throw new NotSupportedException($"No IChannelCreator is registered for configuration type '{configType.FullName}'.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must not be null or empty.", nameof(name));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(ChannelFactory));
    }
}
