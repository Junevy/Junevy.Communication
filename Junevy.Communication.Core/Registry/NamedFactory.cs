using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Core.Registry;

/// <summary>
/// 命名工厂的公共外壳（P25-D12）：名称与别名、按名称获取与移除、释放，以及释放之后的检查，都由 <see cref="NamedRegistry{T}"/> 实现。
/// 派生类只提供创建逻辑（通过 <see cref="GetOrAddCore"/> 与 <see cref="TryAddCore"/>）以及按配置选择创建器的部分。
/// </summary>
/// <remarks>
/// 释放之后，除 <see cref="Dispose()"/> 与 <see cref="DisposeAsync"/> 外的成员都抛出 <see cref="ObjectDisposedException"/>，
/// 其对象名为派生类的名称（<c>GetType().Name</c>）。
/// </remarks>
/// <typeparam name="TItem">工厂管理的实例类型。</typeparam>
public abstract class NamedFactory<TItem> : IDisposable, IAsyncDisposable where TItem : class
{
    private readonly NamedRegistry<TItem> registry;
    private volatile bool disposed;

    /// <summary>创建命名工厂的公共外壳。</summary>
    /// <param name="logger">注册表使用的日志记录器。</param>
    protected NamedFactory(ILogger logger)
    {
        registry = new NamedRegistry<TItem>(logger);
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

    /// <summary>直接注册的实例数（不含别名）。</summary>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return registry.Count;
        }
    }

    /// <summary>按名称获取已注册的实例；别名会解析到目标实例。</summary>
    /// <param name="name">名称或别名。</param>
    /// <param name="item">找到时为实例；否则为 null。</param>
    /// <returns>找到返回 true。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool TryGet(string name, [NotNullWhen(true)] out TItem? item)
    {
        ThrowIfDisposed();
        return registry.TryGet(name, out item);
    }

    /// <summary>按名称获取指定类型的实例。</summary>
    /// <typeparam name="T">期望的实例类型。</typeparam>
    /// <param name="name">名称或别名。</param>
    /// <returns>类型匹配的实例。</returns>
    /// <exception cref="KeyNotFoundException">名称未注册，或别名无法解析。</exception>
    /// <exception cref="InvalidCastException">实例的类型与 <typeparamref name="T"/> 不符。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public T GetRequired<T>(string name) where T : class, TItem
    {
        ThrowIfDisposed();
        return registry.GetRequired<T>(name);
    }

    /// <summary>移除名称对应的实例并释放它。指向该实例的别名一并移除；同一实例仍被其他直接名称引用时不释放。</summary>
    /// <param name="name">名称或别名。</param>
    /// <returns>条目存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool TryRemove(string name)
    {
        ThrowIfDisposed();
        return registry.TryRemove(name);
    }

    /// <summary>注册别名：<paramref name="aliasName"/> 与 <paramref name="existingName"/> 共享同一个实例。</summary>
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

    /// <summary>移除别名。只删除别名本身，不影响目标实例；对直接名称返回 false（直接名称请使用 <see cref="TryRemove"/>）。</summary>
    /// <param name="aliasName">要移除的别名。</param>
    /// <returns>别名存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public bool RemoveAlias(string aliasName)
    {
        ThrowIfDisposed();
        return registry.RemoveAlias(aliasName);
    }

    /// <summary>释放工厂及其全部实例。同步等待每个实例的释放完成（见 <see cref="NamedRegistry{T}.Dispose"/>）。可重复调用。</summary>
    public void Dispose()
    {
        disposed = true;
        registry.Dispose();
    }

    /// <summary>异步释放工厂及其全部实例。可重复调用。</summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync()
    {
        disposed = true;
        return registry.DisposeAsync();
    }

    /// <summary>
    /// 名称已注册时返回已有实例；否则调用 <paramref name="create"/> 创建并注册。释放检查与名称校验在此完成。
    /// </summary>
    /// <param name="name">名称或别名；不能为 null 或空。</param>
    /// <param name="create">创建实例的函数，参数为名称。</param>
    /// <returns>已注册（或刚创建）的实例。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    protected TItem GetOrAddCore(string name, Func<string, TItem> create)
    {
        ThrowIfDisposed();
        ValidateName(name);
        return registry.GetOrAdd(name, create);
    }

    /// <summary>
    /// 名称未注册时调用 <paramref name="create"/> 创建并注册，返回 true；名称已注册（含别名）时不创建，返回 false。
    /// 新建的实例若未能注册（名称在创建期间被占用，或注册表已释放），在此释放。
    /// </summary>
    /// <param name="name">名称；不能为 null 或空。</param>
    /// <param name="create">创建实例的函数，参数为名称。</param>
    /// <param name="item">注册成功时为新建的实例；否则为 null。</param>
    /// <returns>注册成功返回 true。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    protected bool TryAddCore(string name, Func<string, TItem> create, out TItem? item)
    {
        ThrowIfDisposed();
        ValidateName(name);
        if (registry.TryGet(name, out _))
        {
            item = null;
            return false;
        }

        TItem created = create(name);
        bool added = false;
        try
        {
            added = registry.TryAdd(name, created);
        }
        finally
        {
            // 未注册的实例不属于注册表：由本方法释放（同步释放，与通道族既有行为一致）。
            if (!added)
                (created as IDisposable)?.Dispose();
        }

        item = added ? created : null;
        return added;
    }

    /// <summary>若工厂已释放，抛出 <see cref="ObjectDisposedException"/>（对象名为派生类的名称）。</summary>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    protected void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must not be null or empty.", nameof(name));
    }
}
