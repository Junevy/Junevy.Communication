using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 通道工厂的纯内存测试：测试替身创建器创建 <see cref="DuplexClientChannel"/>，不建立套接字。
/// 使用真实 TCP 创建器的工厂测试位于 Junevy.Communication.Tcp.Tests。
/// </summary>
public sealed class ChannelFactoryTests
{
    [Fact(Timeout = 20000)]
    public void DuplicateCreatorForSameConfigType_Throws()
    {
        var creators = new IChannelCreator[] { new StubCreator(), new StubCreator() };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new ChannelFactory(creators));

        Assert.Contains(nameof(StubConfig), ex.Message);
    }

    [Fact(Timeout = 20000)]
    public void Dispose_ThenGetOrAdd_Throws()
    {
        var factory = new ChannelFactory(new IChannelCreator[] { new StubCreator() });
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("line", new StubConfig()));
    }

    [Fact(Timeout = 20000)]
    public void TryAdd_NewName_ReturnsCreatedChannel()
    {
        using var factory = new ChannelFactory(new IChannelCreator[] { new StubCreator() });

        bool added = factory.TryAdd("line", new StubConfig(), out IClientChannel? channel);

        Assert.True(added);
        Assert.NotNull(channel);
        Assert.Same(channel, factory.GetOrAdd("line", new StubConfig()));
    }

    [Fact(Timeout = 20000)]
    public void TryAdd_ExistingName_ReturnsFalseAndKeepsExistingChannel()
    {
        using var factory = new ChannelFactory(new IChannelCreator[] { new StubCreator() });
        IClientChannel existing = factory.GetOrAdd("line", new StubConfig());

        bool added = factory.TryAdd("line", new StubConfig(), out IClientChannel? duplicate);

        Assert.False(added);
        Assert.Null(duplicate);
        Assert.Same(existing, factory.GetOrAdd("line", new StubConfig()));
        Assert.Equal(ConnectionState.Disconnected, existing.State);
    }

    private sealed class StubConfig : IChannelConfig
    {
    }

    private sealed class StubCreator : IChannelCreator
    {
        public Type ConfigType => typeof(StubConfig);

        public IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)
            => new DuplexClientChannel(new ClientChannelSettings { SendTimeout = 2000, RequestTimeout = 2000 }, components);
    }
}
