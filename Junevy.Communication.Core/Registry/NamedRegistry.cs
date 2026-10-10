using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Core.Registry;

/// <summary>
/// 命名实例注册表：支持别名（多个逻辑名共享同一实例，例如 RS-485 多从站共用一条串口）、并发安全与移除时释放。
/// 规则由 <c>ModbusConnectionManager</c> 泛化而来；与其不同之处是所有实例释放都在注册表锁之外进行。
/// </summary>
/// <typeparam name="T">注册的实例类型。释放规则：实现 <see cref="IAsyncDisposable"/> 时优先调用 <c>DisposeAsync</c>，
/// 否则调用 <see cref="IDisposable.Dispose"/>；都不实现则不释放。</typeparam>
/// <remarks>
/// 实例释放（<see cref="TryRemove"/>、<see cref="GetOrAdd"/> 的竞态输家、<see cref="Dispose"/>、<see cref="DisposeAsync"/>）一律在注册表锁之外进行，
/// 因此耗时的释放（例如通道的优雅关闭）不会阻塞其他结构操作，释放回调也可以访问注册表。
/// 同步路径（<see cref="TryRemove"/>、<see cref="GetOrAdd"/>、<see cref="Dispose"/>）会同步等待实例的 <c>DisposeAsync</c> 完成；
/// 实例的异步释放应使用 <c>ConfigureAwait(false)</c>，不依赖调用线程的同步上下文。
/// </remarks>
public sealed class NamedRegistry<T> : IDisposable, IAsyncDisposable where T : class
{
    // 名称 → 直接实例，或别名目标名。
    private readonly ConcurrentDictionary<string, Entry> entries = new ConcurrentDictionary<string, Entry>();

    // 结构变更（移除、建别名、释放）的互斥锁：保证"收集别名 + 删除条目 + 校验目标存在"的原子性。
    // TryAdd / GetOrAdd / TryGet 保持无锁。
    private readonly object registryLock = new object();
    private readonly ILogger logger;
    private volatile bool disposed;

    /// <summary>
    /// 创建注册表。
    /// </summary>
    /// <param name="logger">日志记录器；为 null 时不输出日志。</param>
    public NamedRegistry(ILogger? logger = null)
    {
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>直接实例数（不含别名）。</summary>
    public int Count => entries.Count(kvp => kvp.Value.IsDirect);

    /// <summary>所有名称（含别名）的快照。</summary>
    public IEnumerable<string> Keys => entries.Keys;

    /// <summary>
    /// 按名称获取实例；别名会解析到目标实例。
    /// </summary>
    /// <param name="key">名称或别名。</param>
    /// <param name="value">找到时为实例；否则为 null。</param>
    /// <returns>是否找到可解析的实例。</returns>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
    public bool TryGet(string key, [NotNullWhen(true)] out T? value)
    {
        ThrowIfDisposed();
        value = Resolve(key);
        return value != null;
    }

    /// <summary>
    /// 按名称获取指定类型的实例。
    /// </summary>
    /// <typeparam name="TResult">期望的实例类型。</typeparam>
    /// <param name="key">名称或别名。</param>
    /// <returns>类型匹配的实例。</returns>
    /// <exception cref="KeyNotFoundException">名称未注册，或别名无法解析。</exception>
    /// <exception cref="InvalidCastException">实例类型与 <typeparamref name="TResult"/> 不符。</exception>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
    public TResult GetRequired<TResult>(string key) where TResult : class, T
    {
        if (!TryGet(key, out var value))
            throw new KeyNotFoundException($"No instance is registered under '{key}'.");

        if (value is TResult typed)
            return typed;

        throw new InvalidCastException(
            $"Instance '{key}' is of type '{value.GetType().Name}' and cannot be used as '{typeof(TResult).Name}'.");
    }

    /// <summary>
    /// 注册直接实例。
    /// </summary>
    /// <param name="key">名称；不能为 null 或空。</param>
    /// <param name="value">实例；不能为 null。</param>
    /// <returns>注册成功返回 true；名称已存在返回 false（不替换已有实例，也不释放调用者的实例）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> 为 null 或空。</exception>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
    public bool TryAdd(string key, T value)
    {
        ThrowIfDisposed();
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        ValidateKey(key);

        if (!entries.TryAdd(key, Entry.Direct(value)))
        {
            logger.LogWarning("[TryAdd] Key '{Key}' already exists.", key);
            return false;
        }

        logger.LogDebug("[TryAdd] Registered '{Key}' ({Type}).", key, value.GetType().Name);
        return true;
    }

    /// <summary>
    /// 获取已注册的实例；名称不存在时调用 <paramref name="factory"/> 创建并注册。
    /// 并发竞态时，输家实例立即释放，并返回赢家实例。
    /// </summary>
    /// <param name="key">名称；不能为 null 或空。</param>
    /// <param name="factory">创建实例的工厂，接收名称；不能返回 null。</param>
    /// <returns>已注册（或刚注册）的实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> 为 null 或空。</exception>
    /// <exception cref="InvalidOperationException">工厂返回 null，或名称对应的别名无法解析。</exception>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
    public T GetOrAdd(string key, Func<string, T> factory)
    {
        ThrowIfDisposed();
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));
        ValidateKey(key);

        // ConcurrentDictionary.GetOrAdd 的工厂在竞态下可能多次执行，除赢家外的实例全部泄漏，
        // 因此改为 TryGetValue / TryAdd 自旋：输家立即释放自己创建的实例，然后读取赢家。
        while (true)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                return ResolveFromEntry(key, existing) ?? throw new InvalidOperationException(
                    $"Failed to resolve an instance for key '{key}'.");
            }

            T instance = factory(key) ?? throw new InvalidOperationException(
                $"The factory returned null for key '{key}'.");

            if (entries.TryAdd(key, Entry.Direct(instance)))
                return instance;

            logger.LogWarning("[GetOrAdd] Lost creation race for '{Key}', disposing the duplicate instance.", key);
            ReleaseInstanceAsync(instance, key, "GetOrAdd").GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 移除条目。
    /// 移除直接名称时，级联删除所有直接或间接指向它的别名；若实例不再被任何直接名称引用，则在出锁之后同步释放它。
    /// 同一实例仍被其他直接名称引用时不释放（只在最后一个引用移除时释放）。移除别名时只删除该别名。
    /// </summary>
    /// <param name="key">要移除的名称或别名。</param>
    /// <returns>条目存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
    public bool TryRemove(string key)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(key))
            return false;

        T? released = null;
        lock (registryLock)
        {
            if (!entries.TryGetValue(key, out var entry))
                return false;

            if (!entry.IsDirect)
            {
                entries.TryRemove(key, out _);
                logger.LogInformation("[TryRemove] Removed alias '{Key}' -> '{Target}'.", key, entry.AliasTarget);
                return true;
            }

            // 直接实例：删除条目及所有直接或间接指向它的别名。
            entries.TryRemove(key, out _);
            foreach (var aliasKey in CollectAliasesResolvingTo(key))
            {
                entries.TryRemove(aliasKey, out _);
                logger.LogInformation("[TryRemove] Removed alias '{AliasKey}' -> '{Key}'.", aliasKey, key);
            }

            // 同一实例仍被另一个直接名称引用时不释放，避免重复释放与悬挂引用。
            // 释放推迟到出锁之后：DisposeAsync 可能耗时（例如优雅关闭），不能让它阻塞其他注册表操作。
            T instance = entry.Instance!;
            if (!IsReferencedByDirectEntry(instance))
                released = instance;
        }

        if (released != null)
            ReleaseInstanceAsync(released, key, "TryRemove").GetAwaiter().GetResult();

        return true;
    }

    /// <summary>
    /// 注册别名：<paramref name="aliasKey"/> 解析到 <paramref name="existingKey"/>。
    /// 目标不存在、别名与目标相同，或别名已存在时返回 false，不会留下悬空别名。别名必须在目标注册之后创建。
    /// </summary>
    /// <param name="aliasKey">别名；不能为 null 或空。</param>
    /// <param name="existingKey">目标名称（直接名称或别名）；不能为 null 或空。</param>
    /// <returns>注册成功返回 true；否则返回 false。</returns>
    /// <exception cref="ArgumentException"><paramref name="aliasKey"/> 或 <paramref name="existingKey"/> 为 null 或空。</exception>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
    public bool RegisterAlias(string aliasKey, string existingKey)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(aliasKey))
            throw new ArgumentException("Alias key must not be null or empty.", nameof(aliasKey));
        if (string.IsNullOrEmpty(existingKey))
            throw new ArgumentException("Existing key must not be null or empty.", nameof(existingKey));

        lock (registryLock)
        {
            if (string.Equals(aliasKey, existingKey, StringComparison.Ordinal))
            {
                logger.LogWarning("[RegisterAlias] Alias key '{AliasKey}' equals its target; alias not registered.", aliasKey);
                return false;
            }

            if (!entries.ContainsKey(existingKey))
            {
                logger.LogWarning("[RegisterAlias] Target '{ExistingKey}' is not registered; dangling alias '{AliasKey}' rejected.", existingKey, aliasKey);
                return false;
            }

            if (!entries.TryAdd(aliasKey, Entry.Alias(existingKey)))
            {
                logger.LogWarning("[RegisterAlias] Alias key '{AliasKey}' already exists.", aliasKey);
                return false;
            }
        }

        logger.LogInformation("[RegisterAlias] Alias '{AliasKey}' -> '{ExistingKey}'.", aliasKey, existingKey);
        return true;
    }

    /// <summary>
    /// 移除别名。只删除该别名本身，不影响目标实例；对直接名称返回 false（直接名称请使用 <see cref="TryRemove"/>）。
    /// </summary>
    /// <param name="aliasKey">要移除的别名。</param>
    /// <returns>别名存在并已移除返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
    public bool RemoveAlias(string aliasKey)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(aliasKey))
            return false;

        lock (registryLock)
        {
            if (!entries.TryGetValue(aliasKey, out var entry))
                return false;

            if (entry.IsDirect)
            {
                logger.LogWarning("[RemoveAlias] '{Key}' is not an alias. Use TryRemove to remove a direct entry.", aliasKey);
                return false;
            }

            entries.TryRemove(aliasKey, out _);
            logger.LogInformation("[RemoveAlias] Removed alias '{AliasKey}' -> '{Target}'.", aliasKey, entry.AliasTarget);
            return true;
        }
    }

    /// <summary>
    /// 释放注册表及其全部直接实例（别名随之清空）。同一实例被多个名称引用时只释放一次。
    /// 同步等待每个实例的释放完成。可重复调用。
    /// </summary>
    public void Dispose()
    {
        List<KeyValuePair<string, T>> instances;
        lock (registryLock)
        {
            if (disposed)
                return;

            disposed = true;
            instances = DetachDirectInstances();
        }

        foreach (var item in instances)
            ReleaseInstanceAsync(item.Value, item.Key, "Dispose").GetAwaiter().GetResult();

        logger.LogDebug("[Dispose] NamedRegistry disposed ({Count} instances).", instances.Count);
    }

    /// <summary>
    /// 异步释放注册表及其全部直接实例：并发调用各实例的释放并等待全部完成。可重复调用。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<KeyValuePair<string, T>> instances;
        lock (registryLock)
        {
            if (disposed)
                return;

            disposed = true;
            instances = DetachDirectInstances();
        }

        var releases = new List<Task>(instances.Count);
        foreach (var item in instances)
            releases.Add(ReleaseInstanceAsync(item.Value, item.Key, "DisposeAsync"));

        await Task.WhenAll(releases).ConfigureAwait(false);
        logger.LogDebug("[DisposeAsync] NamedRegistry disposed ({Count} instances).", instances.Count);
    }

    private T? Resolve(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        return entries.TryGetValue(key, out var entry) ? ResolveFromEntry(key, entry) : null;
    }

    private T? ResolveFromEntry(string key, Entry entry)
    {
        if (entry.IsDirect)
            return entry.Instance;

        // 沿别名链解析；visited 记录已经走过的名称，重复出现即为环。
        var visited = new HashSet<string>(StringComparer.Ordinal) { key };
        var current = entry;
        while (!current.IsDirect)
        {
            string target = current.AliasTarget!;
            if (!visited.Add(target))
            {
                logger.LogError("[Resolve] Circular alias detected for '{Key}'.", key);
                return null;
            }

            if (!entries.TryGetValue(target, out var next))
            {
                logger.LogError("[Resolve] Alias target '{Target}' not found for '{Key}'.", target, key);
                return null;
            }

            current = next;
        }

        return current.Instance;
    }

    /// <summary>
    /// 收集所有直接或间接解析到 <paramref name="key"/> 的别名（不含 key 本身）。
    /// 固定算法：S = {key}；每轮把 AliasTarget ∈ S 的别名加入 S，直到无新增。调用方必须持有 <see cref="registryLock"/>。
    /// </summary>
    private List<string> CollectAliasesResolvingTo(string key)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal) { key };
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var kvp in entries)
            {
                if (!kvp.Value.IsDirect
                    && kvp.Value.AliasTarget != null
                    && resolved.Contains(kvp.Value.AliasTarget)
                    && resolved.Add(kvp.Key))
                {
                    added = true;
                }
            }
        }

        resolved.Remove(key);
        return resolved.ToList();
    }

    private bool IsReferencedByDirectEntry(T instance)
        => entries.Values.Any(entry => entry.IsDirect && ReferenceEquals(entry.Instance, instance));

    /// <summary>
    /// 取出全部直接实例（按实例引用去重，保留首个名称）并清空条目。调用方必须持有 <see cref="registryLock"/>。
    /// </summary>
    private List<KeyValuePair<string, T>> DetachDirectInstances()
    {
        var detached = new List<KeyValuePair<string, T>>();
        foreach (var kvp in entries)
        {
            T? instance = kvp.Value.Instance;
            if (instance != null && !ContainsReference(detached, instance))
                detached.Add(new KeyValuePair<string, T>(kvp.Key, instance));
        }

        entries.Clear();
        return detached;
    }

    private static bool ContainsReference(List<KeyValuePair<string, T>> instances, T instance)
    {
        foreach (var item in instances)
        {
            if (ReferenceEquals(item.Value, instance))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 释放单个实例：实现 <see cref="IAsyncDisposable"/> 时优先 <c>DisposeAsync</c>，否则 <see cref="IDisposable"/>；都不实现则不释放。
    /// 释放异常只记录 Warning，不向外抛出，以免一个实例的释放失败阻止其余实例的释放。
    /// </summary>
    private async Task ReleaseInstanceAsync(T instance, string key, string operation)
    {
        try
        {
            if (instance is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (instance is IDisposable disposable)
                disposable.Dispose();
            else
                return;

            logger.LogDebug("[{Operation}] Disposed instance '{Key}'.", operation, key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Operation}] Error disposing instance '{Key}'.", operation, key);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key must not be null or empty.", nameof(key));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(NamedRegistry<T>));
    }

    /// <summary>
    /// 条目：直接实例，或指向另一个名称的别名。
    /// </summary>
    private sealed class Entry
    {
        private Entry(T? instance, string? aliasTarget)
        {
            Instance = instance;
            AliasTarget = aliasTarget;
        }

        public T? Instance { get; }

        public string? AliasTarget { get; }

        public bool IsDirect => Instance != null;

        public static Entry Direct(T instance) => new Entry(instance, null);

        public static Entry Alias(string target) => new Entry(null, target);
    }
}
