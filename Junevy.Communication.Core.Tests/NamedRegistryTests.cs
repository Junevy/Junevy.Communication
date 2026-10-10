using System.Collections.Concurrent;
using Junevy.Communication.Core.Registry;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="NamedRegistry{T}"/> 的测试。前 10 个用例由 Modbus 的 ConnectionManagerAliasTests 泛化移植（规则逐条保留）；
/// 随后是计划新增的并发与释放用例，以及对计划文字规定但表格未列出的规则（GetRequired、环检测、去重释放）的补充用例。
/// </summary>
public sealed class NamedRegistryTests
{
    [Fact]
    public void TryRemove_MasterWithAlias_DisposesInstanceAndRemovesAlias()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m", instance));
        Assert.True(registry.RegisterAlias("a", "m"));

        Assert.True(registry.TryRemove("m"));

        Assert.Equal(1, instance.DisposeCount);
        Assert.False(registry.TryGet("a", out _));
        Assert.Empty(registry.Keys);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryRemove_MasterWithAliasChain_RemovesWholeChain()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m", instance));
        Assert.True(registry.RegisterAlias("a", "m"));
        Assert.True(registry.RegisterAlias("b", "a"));

        Assert.True(registry.TryRemove("m"));

        Assert.Empty(registry.Keys);
        Assert.Equal(1, instance.DisposeCount);
    }

    [Fact]
    public void TryRemove_Alias_KeepsInstance()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m", instance));
        Assert.True(registry.RegisterAlias("a", "m"));

        Assert.True(registry.TryRemove("a"));

        Assert.True(registry.TryGet("m", out var resolved));
        Assert.Same(instance, resolved);
        Assert.Equal(0, instance.DisposeCount);
    }

    [Fact]
    public void TryRemove_SameInstanceUnderTwoDirectKeys_DoesNotDispose()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m1", instance));
        Assert.True(registry.TryAdd("m2", instance));

        Assert.True(registry.TryRemove("m1"));

        Assert.Equal(0, instance.DisposeCount);
        Assert.True(registry.TryGet("m2", out var resolved));
        Assert.Same(instance, resolved);
    }

    [Fact]
    public void RegisterAlias_MissingTarget_ReturnsFalse()
    {
        using var registry = new NamedRegistry<DisposableProbe>();

        Assert.False(registry.RegisterAlias("a", "nope"));
        Assert.Empty(registry.Keys);
    }

    [Fact]
    public void RegisterAlias_SameKey_ReturnsFalse()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        Assert.True(registry.TryAdd("m", new DisposableProbe()));

        Assert.False(registry.RegisterAlias("m", "m"));
    }

    [Fact]
    public void RegisterAlias_ToAlias_Resolves()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m", instance));
        Assert.True(registry.RegisterAlias("a", "m"));

        Assert.True(registry.RegisterAlias("b", "a"));
        Assert.True(registry.TryGet("b", out var resolved));
        Assert.Same(instance, resolved);
    }

    [Fact]
    public void RemoveAlias_DirectEntry_ReturnsFalseAndKeepsEntry()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m", instance));

        Assert.False(registry.RemoveAlias("m"));
        Assert.True(registry.TryGet("m", out var resolved));
        Assert.Same(instance, resolved);
    }

    [Fact]
    public void Dispose_AfterRemovingMasterWithAlias_LeavesNoLeak()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m", instance));
        Assert.True(registry.RegisterAlias("a", "m"));
        Assert.True(registry.TryRemove("m"));

        registry.Dispose();

        Assert.Equal(1, instance.DisposeCount);
    }

    [Fact]
    public async Task Concurrent_AddAliasRemove_PreservesInvariants()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var keys = new[] { "k0", "k1", "k2", "k3" };
        int totalCreated = 0;
        var instances = new ConcurrentBag<DisposableProbe>();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            for (int i = 0; i < 500; i++)
            {
                switch (random.Next(4))
                {
                    case 0:
                        var instance = new DisposableProbe();
                        Interlocked.Increment(ref totalCreated);
                        instances.Add(instance);
                        if (!registry.TryAdd(keys[random.Next(4)], instance))
                            instance.Dispose(); // 键已存在：测试自行释放
                        break;
                    case 1:
                        registry.RegisterAlias("a" + random.Next(4), keys[random.Next(4)]);
                        break;
                    case 2:
                        registry.TryRemove(keys[random.Next(4)]);
                        break;
                    case 3:
                        registry.RemoveAlias("a" + random.Next(4));
                        break;
                }
            }
        }));

        await Task.WhenAll(workers);

        // 不变量 1：Keys 中每个键都必须可解析（无悬空别名）。
        foreach (var key in registry.Keys)
            Assert.True(registry.TryGet(key, out _));

        // 不变量 2：创建总数 - 已释放数 == 存活数 == 注册表直接条目数。
        int disposedCount = instances.Sum(instance => instance.DisposeCount);
        Assert.Equal(totalCreated - disposedCount, registry.Count);
    }

    [Fact]
    public void GetOrAdd_Concurrent_LoserIsDisposed()
    {
        const int threadCount = 32;
        using var registry = new NamedRegistry<DisposableProbe>();

        // 屏障保证 32 个线程都先进入工厂并创建各自的实例，之后才有线程尝试插入：每个线程恰好创建一个实例。
        using var barrier = new Barrier(threadCount);
        var created = new ConcurrentQueue<DisposableProbe>();
        var results = new DisposableProbe?[threadCount];
        var errors = new ConcurrentQueue<Exception>();
        var threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    results[index] = registry.GetOrAdd("shared", _ =>
                    {
                        var probe = new DisposableProbe();
                        created.Enqueue(probe);
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("Not all threads reached the factory.");
                        return probe;
                    });
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            })
            {
                IsBackground = true,
            };
        }

        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.Empty(errors);
        Assert.Equal(threadCount, created.Count);
        Assert.True(registry.TryGet("shared", out var winner));
        Assert.NotNull(winner);
        Assert.Equal(0, winner.DisposeCount);
        Assert.Equal(1, created.Count(probe => probe.DisposeCount == 0));
        Assert.Equal(threadCount - 1, created.Count(probe => probe.DisposeCount == 1));
        Assert.All(results, result => Assert.Same(winner, result));
    }

    [Fact]
    public async Task PrefersDisposeAsync()
    {
        // 三条释放路径（TryRemove、Dispose、DisposeAsync）都只调用 DisposeAsync，不调用 Dispose。
        var removed = new DualDisposableProbe();
        var disposed = new DualDisposableProbe();
        var asyncDisposed = new DualDisposableProbe();

        using (var syncRegistry = new NamedRegistry<DualDisposableProbe>())
        {
            Assert.True(syncRegistry.TryAdd("removed", removed));
            Assert.True(syncRegistry.TryRemove("removed"));
            Assert.Equal(1, removed.DisposeAsyncCount);
            Assert.Equal(0, removed.DisposeCount);

            Assert.True(syncRegistry.TryAdd("disposed", disposed));
        }

        Assert.Equal(1, disposed.DisposeAsyncCount);
        Assert.Equal(0, disposed.DisposeCount);

        var asyncRegistry = new NamedRegistry<DualDisposableProbe>();
        Assert.True(asyncRegistry.TryAdd("async", asyncDisposed));
        await asyncRegistry.DisposeAsync();

        Assert.Equal(1, asyncDisposed.DisposeAsyncCount);
        Assert.Equal(0, asyncDisposed.DisposeCount);
    }

    [Fact]
    public void GetRequired_MissingKey_ThrowsKeyNotFound()
    {
        using var registry = new NamedRegistry<DisposableProbe>();

        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired<DisposableProbe>("missing"));
    }

    [Fact]
    public void GetRequired_WrongType_ThrowsInvalidCast()
    {
        using var registry = new NamedRegistry<object>();
        var instance = new object();
        Assert.True(registry.TryAdd("k", instance));

        Assert.Throws<InvalidCastException>(() => registry.GetRequired<string>("k"));
        Assert.Same(instance, registry.GetRequired<object>("k"));
    }

    [Fact]
    public void Alias_Cycle_ResolvesToNull()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        Assert.True(registry.TryAdd("m", new DisposableProbe()));
        Assert.True(registry.RegisterAlias("a", "m"));
        Assert.True(registry.RegisterAlias("b", "a"));

        // RemoveAlias 只删除别名本身，b 随之悬空；随后以 b 为目标注册 a，形成 a → b → a 的环。
        Assert.True(registry.RemoveAlias("a"));
        Assert.True(registry.RegisterAlias("a", "b"));

        Assert.False(registry.TryGet("a", out _));
        Assert.False(registry.TryGet("b", out _));
        Assert.True(registry.TryGet("m", out _));
    }

    [Fact]
    public void TryAdd_DuplicateKey_ReturnsFalseAndKeepsFirst()
    {
        using var registry = new NamedRegistry<DisposableProbe>();
        var first = new DisposableProbe();
        Assert.True(registry.TryAdd("k", first));

        Assert.False(registry.TryAdd("k", new DisposableProbe()));
        Assert.True(registry.TryGet("k", out var resolved));
        Assert.Same(first, resolved);
    }

    [Fact]
    public void GetOrAdd_FactoryReturnsNull_Throws()
    {
        using var registry = new NamedRegistry<DisposableProbe>();

        Assert.Throws<InvalidOperationException>(() => registry.GetOrAdd("k", _ => null!));
        Assert.Empty(registry.Keys);
    }

    [Fact]
    public void Dispose_SharedInstance_DisposedOnce()
    {
        var registry = new NamedRegistry<DisposableProbe>();
        var instance = new DisposableProbe();
        Assert.True(registry.TryAdd("m1", instance));
        Assert.True(registry.TryAdd("m2", instance));

        registry.Dispose();

        Assert.Equal(1, instance.DisposeCount);
    }

    /// <summary>仅记录 Dispose 次数的测试替身。</summary>
    private sealed class DisposableProbe : IDisposable
    {
        private int disposeCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public void Dispose() => Interlocked.Increment(ref disposeCount);
    }

    /// <summary>同时实现两种释放接口的测试替身，用于验证优先调用 DisposeAsync。</summary>
    private sealed class DualDisposableProbe : IDisposable, IAsyncDisposable
    {
        private int disposeCount;
        private int disposeAsyncCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public int DisposeAsyncCount => Volatile.Read(ref disposeAsyncCount);

        public void Dispose() => Interlocked.Increment(ref disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeAsyncCount);
            return default;
        }
    }
}
