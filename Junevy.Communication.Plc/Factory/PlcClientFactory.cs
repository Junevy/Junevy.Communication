using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Plc;

/// <summary>
/// 协议客户端工厂：按名称创建、获取与移除地址型 PLC 客户端。创建时按配置的精确运行时类型选择 <see cref="IPlcClientCreator"/>。
/// 行为与通道族的 <c>ChannelFactory</c> 一致。
/// </summary>
/// <remarks>
/// 名称与别名由 <see cref="NamedRegistry{T}"/> 管理：同名获取返回同一实例，别名与其目标共享实例，移除或释放时释放客户端。
/// 配置非法时由创建器抛出 <see cref="ArgumentException"/>。工厂释放之后，除 <see cref="Dispose()"/> 与 <see cref="DisposeAsync"/> 外的成员都抛出 <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class PlcClientFactory : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<Type, IPlcClientCreator> creatorsByConfigType = new Dictionary<Type, IPlcClientCreator>();
    private readonly ILoggerFactory loggerFactory;
    private readonly NamedRegistry<IPlcClient> registry;
    private volatile bool disposed;

    /// <summary>
    /// 创建客户端工厂。
    /// </summary>
    /// <param name="creators">客户端创建器；每种配置类型只能注册一个。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="creators"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="creators"/> 含有 null，或同一配置类型注册了多个创建器。</exception>
    public PlcClientFactory(IEnumerable<IPlcClientCreator> creators, ILoggerFactory? loggerFactory = null)
    {
        if (creators == null)
            throw new ArgumentNullException(nameof(creators));

        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        foreach (IPlcClientCreator creator in creators)
        {
            if (creator == null)
                throw new ArgumentException("The creator collection must not contain null.", nameof(creators));

            Type configType = creator.ConfigType;
            if (creatorsByConfigType.ContainsKey(configType))
                throw new ArgumentException(
                    $"More than one IPlcClientCreator is registered for configuration type '{configType.FullName}'.", nameof(creators));

            creatorsByConfigType.Add(configType, creator);
        }

        registry = new NamedRegistry<IPlcClient>(this.loggerFactory.CreateLogger<PlcClientFactory>());
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

    /// <summary>直接注册的客户端数（不含别名）。</summary>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return registry.Count;
        }
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
        IPlcClientCreator creator = ResolveCreator(config);
        ValidateName(name);

        return registry.GetOrAdd(name, key => creator.Create(key, config, loggerFactory));
    }

    /// <summary>按名称获取已注册的客户端；别名会解析到目标客户端。</summary>
    /// <param name="name">客户端名称或别名。</param>
    /// <param name="client">找到时为客户端；否则为 null。</param>
    /// <returns>找到返回 true。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool TryGet(string name, [NotNullWhen(true)] out IPlcClient? client)
    {
        ThrowIfDisposed();
        return registry.TryGet(name, out client);
    }

    /// <summary>按名称获取指定类型的客户端。</summary>
    /// <typeparam name="T">期望的客户端类型。</typeparam>
    /// <param name="name">客户端名称或别名。</param>
    /// <returns>类型匹配的客户端。</returns>
    /// <exception cref="KeyNotFoundException">名称未注册，或别名无法解析。</exception>
    /// <exception cref="InvalidCastException">客户端的类型与 <typeparamref name="T"/> 不符。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public T GetRequired<T>(string name) where T : class, IPlcClient
    {
        ThrowIfDisposed();
        return registry.GetRequired<T>(name);
    }

    /// <summary>
    /// 移除名称对应的客户端并释放它。指向该客户端的别名一并移除；同一客户端仍被其他直接名称引用时不释放。
    /// </summary>
    /// <param name="name">客户端名称或别名。</param>
    /// <returns>条目存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool TryRemove(string name)
    {
        ThrowIfDisposed();
        return registry.TryRemove(name);
    }

    /// <summary>注册别名：<paramref name="aliasName"/> 与 <paramref name="existingName"/> 共享同一个客户端。</summary>
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

    /// <summary>移除别名。只删除别名本身，不影响目标客户端；对直接名称返回 false（直接名称请使用 <see cref="TryRemove"/>）。</summary>
    /// <param name="aliasName">要移除的别名。</param>
    /// <returns>别名存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool RemoveAlias(string aliasName)
    {
        ThrowIfDisposed();
        return registry.RemoveAlias(aliasName);
    }

    /// <summary>释放工厂及其全部客户端。同步等待每个客户端的释放完成。可重复调用。</summary>
    public void Dispose()
    {
        disposed = true;
        registry.Dispose();
    }

    /// <summary>异步释放工厂及其全部客户端。可重复调用。</summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync()
    {
        disposed = true;
        return registry.DisposeAsync();
    }

    // 按配置的精确运行时类型选择创建器；不沿基类链查找，子类型配置需要自己的创建器。
    private IPlcClientCreator ResolveCreator(IPlcClientConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        Type configType = config.GetType();
        if (creatorsByConfigType.TryGetValue(configType, out IPlcClientCreator? creator))
            return creator;

        throw new NotSupportedException($"No IPlcClientCreator is registered for configuration type '{configType.FullName}'.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must not be null or empty.", nameof(name));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(PlcClientFactory));
    }
}
