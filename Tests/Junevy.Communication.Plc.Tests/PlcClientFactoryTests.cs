using Junevy.Communication.Channels;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 客户端工厂的纯内存测试：测试替身创建器创建 <see cref="FakePlcClient"/>，不建立套接字。覆盖面参照 P1 的 ChannelFactoryTests。
/// </summary>
public sealed class PlcClientFactoryTests
{
    [Fact(Timeout = 20000)]
    public void DuplicateCreatorForSameConfigType_Throws()
    {
        var creators = new IPlcClientCreator[] { new StubCreator(), new StubCreator() };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new PlcClientFactory(creators));

        Assert.Contains(nameof(StubConfig), ex.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 20000)]
    public void NullCreatorInTheCollection_Throws()
    {
        var creators = new IPlcClientCreator[] { null! };

        Assert.Throws<ArgumentException>(() => new PlcClientFactory(creators));
    }

    [Fact(Timeout = 20000)]
    public void NullCreators_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PlcClientFactory(null!));
    }

    [Fact(Timeout = 20000)]
    public void Dispose_ThenGetOrAdd_Throws()
    {
        var factory = new PlcClientFactory(new IPlcClientCreator[] { new StubCreator() });
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("line", new StubConfig()));
    }

    [Fact(Timeout = 20000)]
    public void GetOrAdd_NewName_CreatesTheClientOnceAndRegistersIt()
    {
        var creator = new StubCreator();
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { creator });

        IPlcClient client = factory.GetOrAdd("line", new StubConfig());

        Assert.IsType<FakePlcClient>(client);
        Assert.Same(client, factory.GetOrAdd("line", new StubConfig()));
        Assert.Equal(1, creator.CreateCount);
        Assert.Equal(new[] { "line" }, factory.Keys);
        Assert.Equal(1, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void GetOrAdd_ExistingName_IgnoresTheNewConfig()
    {
        var creator = new StubCreator();
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { creator });
        IPlcClient first = factory.GetOrAdd("line", new StubConfig { Tag = "first" });

        IPlcClient second = factory.GetOrAdd("line", new StubConfig { Tag = "second" });

        Assert.Same(first, second);
        Assert.Equal(1, creator.CreateCount);
    }

    [Fact(Timeout = 20000)]
    public void GetOrAdd_ConfigWithoutAnExactCreator_ThrowsNotSupportedException()
    {
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { new StubCreator() });

        Assert.Throws<NotSupportedException>(() => factory.GetOrAdd("line", new DerivedStubConfig()));
    }

    [Fact(Timeout = 20000)]
    public void GetOrAdd_NullConfig_ThrowsArgumentNullException()
    {
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { new StubCreator() });

        Assert.Throws<ArgumentNullException>(() => factory.GetOrAdd("line", null!));
    }

    [Fact(Timeout = 20000)]
    public void GetOrAdd_EmptyName_ThrowsArgumentException()
    {
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { new StubCreator() });

        Assert.Throws<ArgumentException>(() => factory.GetOrAdd(string.Empty, new StubConfig()));
    }

    [Fact(Timeout = 20000)]
    public void TryGet_FindsTheClientByNameAndAlias()
    {
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { new StubCreator() });
        IPlcClient client = factory.GetOrAdd("line", new StubConfig());
        Assert.True(factory.RegisterAlias("alias", "line"));

        Assert.True(factory.TryGet("alias", out IPlcClient? viaAlias));
        Assert.Same(client, viaAlias);
        Assert.False(factory.TryGet("missing", out _));
    }

    [Fact(Timeout = 20000)]
    public void GetRequired_ReturnsTheTypedClient_AndRejectsMissingOrWrongTypes()
    {
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { new StubCreator() });
        IPlcClient client = factory.GetOrAdd("line", new StubConfig());

        Assert.Same(client, factory.GetRequired<FakePlcClient>("line"));
        Assert.Throws<KeyNotFoundException>(() => factory.GetRequired<FakePlcClient>("missing"));
        Assert.Throws<InvalidCastException>(() => factory.GetRequired<OtherClient>("line"));
    }

    [Fact(Timeout = 20000)]
    public void TryRemove_DisposesTheClient_AndRemovesItsAliases()
    {
        var creator = new StubCreator();
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { creator });
        factory.GetOrAdd("line", new StubConfig());
        Assert.True(factory.RegisterAlias("alias", "line"));

        Assert.True(factory.TryRemove("line"));

        Assert.Equal(1, creator.Channels[0].DisposeCount);
        Assert.False(factory.TryGet("alias", out _));
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void RegisterAlias_MissingTarget_ReturnsFalse()
    {
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { new StubCreator() });

        Assert.False(factory.RegisterAlias("alias", "missing"));
        Assert.False(factory.TryGet("alias", out _));
    }

    [Fact(Timeout = 20000)]
    public void RemoveAlias_KeepsTheTargetClient_AndRefusesDirectNames()
    {
        var creator = new StubCreator();
        using var factory = new PlcClientFactory(new IPlcClientCreator[] { creator });
        factory.GetOrAdd("line", new StubConfig());
        Assert.True(factory.RegisterAlias("alias", "line"));

        Assert.True(factory.RemoveAlias("alias"));
        Assert.False(factory.RemoveAlias("line"));

        Assert.True(factory.TryGet("line", out _));
        Assert.Equal(0, creator.Channels[0].DisposeCount);
    }

    [Fact(Timeout = 20000)]
    public async Task DisposeAsync_DisposesEveryClient()
    {
        var creator = new StubCreator();
        var factory = new PlcClientFactory(new IPlcClientCreator[] { creator });
        factory.GetOrAdd("first", new StubConfig());
        factory.GetOrAdd("second", new StubConfig());

        await factory.DisposeAsync();

        Assert.Equal(1, creator.Channels[0].DisposeCount);
        Assert.Equal(1, creator.Channels[1].DisposeCount);
    }

    // 与工厂中的客户端类型不同的派生类型，用于验证 GetRequired 的类型检查。
    private sealed class OtherClient : FakePlcClient
    {
        public OtherClient(IClientChannel channel)
            : base(channel)
        {
        }
    }
}
