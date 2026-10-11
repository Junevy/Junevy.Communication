using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="NamedFactory{TItem}"/> 的外壳行为：创建、注册、别名、移除、释放与释放后的检查（P25-D12）。
/// </summary>
public sealed class NamedFactoryTests
{
    [Fact]
    public void GetOrAddCore_CreatesOnceAndReturnsTheSameInstanceForTheSameName()
    {
        using var factory = new TestNamedFactory();

        TestItem first = factory.GetOrAdd("alpha");
        TestItem second = factory.GetOrAdd("alpha");

        Assert.Same(first, second);
        Assert.Equal("alpha", first.Name);
        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(1, factory.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetOrAddCore_NullOrEmptyName_ThrowsArgumentException(string? name)
    {
        using var factory = new TestNamedFactory();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => factory.GetOrAdd(name!));

        Assert.StartsWith("Name must not be null or empty.", exception.Message);
        Assert.Equal("name", exception.ParamName);
        Assert.Equal(0, factory.CreatedCount);
    }

    [Fact]
    public void TryAddCore_NewName_RegistersAndReturnsTheItem()
    {
        using var factory = new TestNamedFactory();

        Assert.True(factory.Add("alpha", out TestItem? item));

        Assert.NotNull(item);
        Assert.True(factory.TryGet("alpha", out TestItem? found));
        Assert.Same(item, found);
    }

    [Fact]
    public void TryAddCore_ExistingName_ReturnsFalseWithoutCreating()
    {
        using var factory = new TestNamedFactory();
        factory.Add("alpha", out _);

        Assert.False(factory.Add("alpha", out TestItem? item));

        Assert.Null(item);
        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public void TryAddCore_AliasName_ReturnsFalseWithoutCreating()
    {
        using var factory = new TestNamedFactory();
        factory.GetOrAdd("alpha");
        factory.RegisterAlias("beta", "alpha");

        Assert.False(factory.Add("beta", out TestItem? item));

        Assert.Null(item);
        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public void Count_CountsDirectNames_AndKeysIncludeAliases()
    {
        using var factory = new TestNamedFactory();
        factory.GetOrAdd("alpha");
        factory.GetOrAdd("beta");
        factory.RegisterAlias("gamma", "alpha");

        Assert.Equal(2, factory.Count);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, factory.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void TryGet_FindsDirectNamesAndResolvesAliases()
    {
        using var factory = new TestNamedFactory();
        TestItem alpha = factory.GetOrAdd("alpha");
        factory.RegisterAlias("gamma", "alpha");

        Assert.True(factory.TryGet("gamma", out TestItem? viaAlias));
        Assert.Same(alpha, viaAlias);
        Assert.False(factory.TryGet("missing", out TestItem? missing));
        Assert.Null(missing);
    }

    [Fact]
    public void GetRequired_ReturnsTheRegisteredInstance_AndThrowsForAnUnknownName()
    {
        using var factory = new TestNamedFactory();
        TestItem alpha = factory.GetOrAdd("alpha");

        Assert.Same(alpha, factory.GetRequired<TestItem>("alpha"));
        Assert.Throws<KeyNotFoundException>(() => factory.GetRequired<TestItem>("missing"));
    }

    [Fact]
    public void TryRemove_RemovesTheEntryAndDisposesTheInstance()
    {
        using var factory = new TestNamedFactory();
        TestItem alpha = factory.GetOrAdd("alpha");

        Assert.True(factory.TryRemove("alpha"));

        Assert.Equal(1, alpha.DisposeCount);
        Assert.False(factory.TryRemove("alpha"));
    }

    [Fact]
    public void Aliases_RegisterOnlyAgainstAnExistingTarget_AndRemovingTheAliasKeepsTheTarget()
    {
        using var factory = new TestNamedFactory();
        TestItem alpha = factory.GetOrAdd("alpha");

        Assert.False(factory.RegisterAlias("gamma", "missing"));
        Assert.True(factory.RegisterAlias("gamma", "alpha"));
        Assert.True(factory.RemoveAlias("gamma"));

        Assert.True(factory.TryGet("alpha", out TestItem? stillThere));
        Assert.Same(alpha, stillThere);
        Assert.Equal(0, alpha.DisposeCount);
        Assert.False(factory.RemoveAlias("alpha"));
    }

    [Fact]
    public void Dispose_DisposesEveryInstanceOnce_AndLaterCallsNameTheDerivedType()
    {
        var factory = new TestNamedFactory();
        TestItem alpha = factory.GetOrAdd("alpha");
        TestItem beta = factory.GetOrAdd("beta");

        factory.Dispose();
        factory.Dispose();

        Assert.Equal(1, alpha.DisposeCount);
        Assert.Equal(1, beta.DisposeCount);
        AssertDisposed(() => factory.CheckDisposed());
        AssertDisposed(() => factory.Count);
        AssertDisposed(() => factory.Keys);
        AssertDisposed(() => factory.GetOrAdd("gamma"));
        AssertDisposed(() => factory.Add("gamma", out _));
        AssertDisposed(() => factory.TryGet("alpha", out _));
        AssertDisposed(() => factory.GetRequired<TestItem>("alpha"));
        AssertDisposed(() => factory.TryRemove("alpha"));
        AssertDisposed(() => factory.RegisterAlias("delta", "alpha"));
        AssertDisposed(() => factory.RemoveAlias("delta"));
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryInstance_AndLaterCallsNameTheDerivedType()
    {
        var factory = new TestNamedFactory();
        TestItem alpha = factory.GetOrAdd("alpha");

        await factory.DisposeAsync();

        Assert.Equal(1, alpha.DisposeCount);
        AssertDisposed(() => factory.Count);
    }

    private static void AssertDisposed(Func<object?> action)
    {
        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(action);

        Assert.Equal(nameof(TestNamedFactory), exception.ObjectName);
    }

    private sealed class TestNamedFactory : NamedFactory<TestItem>
    {
        public TestNamedFactory()
            : base(NullLogger.Instance)
        {
        }

        public int CreatedCount { get; private set; }

        public TestItem GetOrAdd(string name) => GetOrAddCore(name, Create);

        public bool Add(string name, out TestItem? item) => TryAddCore(name, Create, out item);

        public object? CheckDisposed()
        {
            ThrowIfDisposed();
            return null;
        }

        private TestItem Create(string name)
        {
            CreatedCount++;
            return new TestItem(name);
        }
    }

    private sealed class TestItem : IDisposable
    {
        public TestItem(string name) => Name = name;

        public string Name { get; }

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
