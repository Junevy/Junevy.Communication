using Junevy.Communication.Core.Registry;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="CreatorCatalog{TCreator}"/> 的精确配置类型选择与参数校验（P25-D12）。
/// </summary>
public sealed class CreatorCatalogTests
{
    [Fact]
    public void Constructor_NullCreators_ThrowsArgumentNullException()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new CreatorCatalog<FakeCreator>(null!));

        Assert.Equal("creators", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullCreator_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new CreatorCatalog<FakeCreator>(new FakeCreator[] { new FakeCreator(typeof(BaseConfig)), null! }));

        Assert.StartsWith("The creator collection must not contain null.", exception.Message);
        Assert.Equal("creators", exception.ParamName);
    }

    [Fact]
    public void Constructor_TwoCreatorsForOneConfigType_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new CreatorCatalog<FakeCreator>(new[]
        {
            new FakeCreator(typeof(BaseConfig)),
            new FakeCreator(typeof(BaseConfig)),
        }));

        Assert.StartsWith($"More than one FakeCreator is registered for configuration type '{typeof(BaseConfig).FullName}'.", exception.Message);
        Assert.Equal("creators", exception.ParamName);
    }

    [Fact]
    public void Resolve_ReturnsTheCreatorRegisteredForTheExactConfigType()
    {
        var baseCreator = new FakeCreator(typeof(BaseConfig));
        var otherCreator = new FakeCreator(typeof(OtherConfig));
        var catalog = new CreatorCatalog<FakeCreator>(new[] { baseCreator, otherCreator });

        Assert.Same(baseCreator, catalog.Resolve(new BaseConfig()));
        Assert.Same(otherCreator, catalog.Resolve(new OtherConfig()));
    }

    [Fact]
    public void Resolve_NullConfig_ThrowsArgumentNullException()
    {
        var catalog = new CreatorCatalog<FakeCreator>(Array.Empty<FakeCreator>());

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => catalog.Resolve(null!));

        Assert.Equal("config", exception.ParamName);
    }

    [Fact]
    public void Resolve_UnregisteredConfigType_ThrowsNotSupportedException()
    {
        var catalog = new CreatorCatalog<FakeCreator>(new[] { new FakeCreator(typeof(BaseConfig)) });

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => catalog.Resolve(new OtherConfig()));

        Assert.Equal($"No FakeCreator is registered for configuration type '{typeof(OtherConfig).FullName}'.", exception.Message);
    }

    [Fact]
    public void Resolve_DerivedConfigType_IsNotMatchedToTheBaseCreator()
    {
        var catalog = new CreatorCatalog<FakeCreator>(new[] { new FakeCreator(typeof(BaseConfig)) });

        Assert.Throws<NotSupportedException>(() => catalog.Resolve(new DerivedConfig()));
    }

    [Fact]
    public void TryResolve_FindsOnlyTheExactConfigType()
    {
        var creator = new FakeCreator(typeof(BaseConfig));
        var catalog = new CreatorCatalog<FakeCreator>(new[] { creator });

        Assert.True(catalog.TryResolve(typeof(BaseConfig), out FakeCreator? found));
        Assert.Same(creator, found);
        Assert.False(catalog.TryResolve(typeof(DerivedConfig), out FakeCreator? missing));
        Assert.Null(missing);
    }

    private sealed class FakeCreator : IConfigTypedCreator
    {
        public FakeCreator(Type configType) => ConfigType = configType;

        public Type ConfigType { get; }
    }

    private class BaseConfig
    {
    }

    private sealed class DerivedConfig : BaseConfig
    {
    }

    private sealed class OtherConfig
    {
    }
}
