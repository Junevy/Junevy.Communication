using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>
    /// Manages named Modbus instances. Supports aliasing so that multiple logical names
    /// can share a single physical connection — essential for RS-485 multi-drop RTU setups.
    /// </summary>
    public sealed class ModbusConnectionManager : IModbusConnectionManager
    {
        // Maps a name to either a direct IModbus instance or an alias target name.
        private readonly ConcurrentDictionary<string, Entry> entries = new();

        // 别名结构变更（TryRemove / RegisterAlias / RemoveAlias）的互斥锁：
        // 保证"收集别名 + 删除条目 + 校验目标存在"的原子性。Add / GetOrAdd / Get / TryGet 保持无锁。
        private readonly object registryLock = new object();
        private readonly ILogger<ModbusConnectionManager> logger;
        private bool disposed;

        public int Count => entries.Count(kvp => kvp.Value.IsDirect);
        public IEnumerable<string> Keys => entries.Keys;

        public ModbusConnectionManager(ILogger<ModbusConnectionManager>? logger = null)
        {
            this.logger = logger ?? NullLogger<ModbusConnectionManager>.Instance;
        }

        public IModbus? Get(string key)
        {
            ThrowIfDisposed();
            return Resolve(key);
        }

        public TResult GetRequired<TResult>(string key) where TResult : class, IModbus
        {
            var modbus = Get(key);
            if (modbus is TResult typed)
                return typed;
            throw new InvalidOperationException(
                $"Modbus instance '{key}' not found or type mismatch. Expected {typeof(TResult).Name}.");
        }

        public bool TryGet(string key, out IModbus? modbus)
        {
            ThrowIfDisposed();
            modbus = Resolve(key);
            return modbus != null;
        }

        public bool Add(string key, IModbus modbus)
        {
            ThrowIfDisposed();
            if (modbus == null)
                throw new ArgumentNullException(nameof(modbus));

            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));

            var entry = new Entry(modbus);
            if (!entries.TryAdd(key, entry))
            {
                logger.LogWarning(" [Add] Key '{Key}' already exists.", key);
                return false;
            }

            logger.LogDebug(" [Add] Registered '{Key}' ({Type}).", key, modbus.GetType().Name);
            return true;
        }

        public IModbus GetOrAdd(string key, Func<string, IModbus> factory)
        {
            ThrowIfDisposed();
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));

            // ConcurrentDictionary.GetOrAdd 的 valueFactory 在竞态下可能执行多次，
            // 会创建多份连接实例且除赢家外全部泄漏 —— 改为 TryGetValue/TryAdd 自旋，
            // 输家立即释放自己创建的实例。
            while (true)
            {
                if (entries.TryGetValue(key, out var existing))
                {
                    return ResolveFromEntry(key, existing) ?? throw new InvalidOperationException(
                        $"Failed to resolve Modbus instance for key '{key}'.");
                }

                var instance = factory(key);
                if (entries.TryAdd(key, new Entry(instance)))
                    return instance;

                try
                {
                    instance.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, " [GetOrAdd] Disposed losing instance for '{Key}' with error.", key);
                }
                logger.LogWarning(" [GetOrAdd] Lost creation race for '{Key}', disposed duplicate instance.", key);
            }
        }

        public bool TryRemove(string key)
        {
            ThrowIfDisposed();

            lock (registryLock)
            {
                if (!entries.TryGetValue(key, out var entry))
                    return false;

                if (!entry.IsDirect)
                {
                    entries.TryRemove(key, out _);
                    logger.LogInformation(" [TryRemove] Removed alias '{Key}' -> '{Target}'.", key, entry.AliasTarget);
                    return true;
                }

                // 直接实例：删除条目及所有直接或间接指向它的别名，然后释放实例
                entries.TryRemove(key, out _);
                foreach (var aliasKey in CollectAliasesResolvingTo(key))
                {
                    entries.TryRemove(aliasKey, out _);
                    logger.LogInformation(" [TryRemove] Removed alias '{AliasKey}' -> '{Key}'.", aliasKey, key);
                }

                // 同一实例还被另一个直接条目引用时不释放，避免重复释放/悬挂引用
                bool stillReferenced = entries.Values.Any(e =>
                    e.IsDirect && ReferenceEquals(e.Instance, entry.Instance));
                if (!stillReferenced)
                {
                    try
                    {
                        entry.Instance!.Dispose();
                        logger.LogInformation(" [TryRemove] Disposed instance '{Key}'.", key);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, " [TryRemove] Error disposing instance '{Key}'.", key);
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// 收集所有直接或间接解析到 <paramref name="key"/> 的别名键（不含 key 本身）。
        /// 固定算法：S = {key}；每轮把 AliasTarget ∈ S 的别名键加入 S，直到无新增。
        /// 调用方必须持有 <see cref="registryLock"/>。
        /// </summary>
        private List<string> CollectAliasesResolvingTo(string key)
        {
            var resolved = new HashSet<string> { key };
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
                    logger.LogWarning(" [RegisterAlias] Alias key '{AliasKey}' equals its target; alias not registered.", aliasKey);
                    return false;
                }

                if (!entries.ContainsKey(existingKey))
                {
                    logger.LogWarning(" [RegisterAlias] Target '{ExistingKey}' is not registered; dangling alias '{AliasKey}' rejected.", existingKey, aliasKey);
                    return false;
                }

                if (!entries.TryAdd(aliasKey, new Entry(existingKey))) // alias, not direct
                {
                    logger.LogWarning(" [RegisterAlias] Alias key '{AliasKey}' already exists.", aliasKey);
                    return false;
                }
            }

            logger.LogInformation(" [RegisterAlias] Alias '{AliasKey}' -> '{ExistingKey}'.", aliasKey, existingKey);
            return true;
        }

        public bool RemoveAlias(string aliasKey)
        {
            ThrowIfDisposed();

            lock (registryLock)
            {
                if (!entries.TryGetValue(aliasKey, out var entry))
                    return false;

                if (entry.IsDirect)
                {
                    logger.LogWarning(" [RemoveAlias] '{Key}' is not an alias. Use TryRemove to remove a master entry.", aliasKey);
                    return false;
                }

                entries.TryRemove(aliasKey, out _);
                logger.LogInformation(" [RemoveAlias] Removed alias '{AliasKey}' -> '{Target}'.", aliasKey, entry.AliasTarget);
                return true;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            int count = entries.Count(kvp => kvp.Value.IsDirect);

            // Dispose only direct instances (aliases just point to them)
            var disposedInstances = new HashSet<IModbus>();
            foreach (var kvp in entries)
            {
                if (kvp.Value.IsDirect && kvp.Value.Instance != null && !disposedInstances.Contains(kvp.Value.Instance))
                {
                    try
                    {
                        kvp.Value.Instance.Dispose();
                        disposedInstances.Add(kvp.Value.Instance);
                        logger.LogDebug(" [Dispose] Disposed instance '{Key}'.", kvp.Key);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, " [Dispose] Error disposing instance '{Key}'.", kvp.Key);
                    }
                }
            }
            entries.Clear();
            logger.LogInformation(" [Dispose] ModbusConnectionManager disposed ({Count} instances).", count);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;

            int count = entries.Count(kvp => kvp.Value.IsDirect);

            var disposedInstances = new HashSet<IModbus>();
            var tasks = new List<Task>();

            foreach (var kvp in entries)
            {
                if (kvp.Value.IsDirect && kvp.Value.Instance != null && !disposedInstances.Contains(kvp.Value.Instance))
                {
                    disposedInstances.Add(kvp.Value.Instance);
                    try
                    {
                        if (kvp.Value.Instance is IAsyncDisposable ad)
                            tasks.Add(ad.DisposeAsync().AsTask());
                        else
                            kvp.Value.Instance.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, " [DisposeAsync] Error disposing instance '{Key}'.", kvp.Key);
                    }
                }
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);

            entries.Clear();
            logger.LogInformation(" [DisposeAsync] ModbusConnectionManager disposed ({Count} instances).", count);
        }

        private IModbus? Resolve(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            if (!entries.TryGetValue(key, out var entry))
                return null;

            return ResolveFromEntry(key, entry);
        }

        private IModbus? ResolveFromEntry(string key, Entry entry)
        {
            if (entry.IsDirect)
                return entry.Instance;

            // Follow alias chain (with loop detection)
            var visited = new HashSet<string> { key };
            var current = entry;
            var currentKey = entry.AliasTarget!;

            while (current is { IsDirect: false, AliasTarget: not null })
            {
                if (!visited.Add(current.AliasTarget))
                {
                    logger.LogError(" [Resolve] Circular alias detected for '{Key}'.", key);
                    return null;
                }
                currentKey = current.AliasTarget;
                if (!entries.TryGetValue(currentKey, out current))
                {
                    logger.LogError(" [Resolve] Alias target '{Target}' not found for '{Key}'.", currentKey, key);
                    return null;
                }
                if (current.IsDirect)
                    return current.Instance;
            }

            return null;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ModbusConnectionManager));
        }

        /// <summary>
        /// Represents either a direct Modbus instance or an alias to another entry.
        /// </summary>
        private sealed class Entry
        {
            public IModbus? Instance { get; }
            public string? AliasTarget { get; }
            public bool IsDirect => Instance != null;

            public Entry(IModbus instance)
            {
                Instance = instance;
                AliasTarget = null;
            }

            public Entry(string aliasTarget)
            {
                Instance = null;
                AliasTarget = aliasTarget;
            }
        }
    }
}
