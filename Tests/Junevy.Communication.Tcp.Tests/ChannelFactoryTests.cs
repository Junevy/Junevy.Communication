using Junevy.Communication.Channels;
using Junevy.Communication.Tcp.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 通道工厂与依赖注入，使用真实的 TCP 创建器。这些用例只构造通道、不建立连接，因此不在 SocketTiming 集合中。
/// </summary>
public sealed class ChannelFactoryTests
{
    [Fact(Timeout = 30000)]
    public void GetOrAdd_CreatesByConfigType()
    {
        using var factory = new ChannelFactory(new IChannelCreator[] { new TcpClientChannelCreator() });
        TcpClientChannelConfig config = TcpTestHelpers.CreateConfig(5000);

        IClientChannel channel = factory.GetOrAdd("plc", config);

        Assert.IsType<TcpClientChannel>(channel);
        Assert.Equal("plc", channel.Name);
        Assert.Same(channel, factory.GetOrAdd("plc", config));
        Assert.Equal(1, factory.Count);
    }

    [Fact(Timeout = 30000)]
    public void UnknownConfigType_ThrowsNotSupported()
    {
        using var factory = new ChannelFactory(new IChannelCreator[] { new TcpClientChannelCreator() });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => factory.GetOrAdd("serial", new UnregisteredConfig()));

        Assert.Contains(nameof(UnregisteredConfig), ex.Message);
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 30000)]
    public void DerivedConfigType_IsNotMatched_ThrowsNotSupported()
    {
        using var factory = new ChannelFactory(new IChannelCreator[] { new TcpClientChannelCreator() });

        Assert.Throws<NotSupportedException>(() => factory.GetOrAdd("derived", new DerivedTcpConfig()));
    }

    [Fact(Timeout = 30000)]
    public void Alias_SharesInstance()
    {
        using var factory = new ChannelFactory(new IChannelCreator[] { new TcpClientChannelCreator() });
        TcpClientChannelConfig config = TcpTestHelpers.CreateConfig(5000);
        IClientChannel primary = factory.GetOrAdd("plc", config);

        Assert.True(factory.RegisterAlias("plc-alias", "plc"));

        Assert.True(factory.TryGet("plc-alias", out IClientChannel? aliased));
        Assert.Same(primary, aliased);
        Assert.Same(primary, factory.GetOrAdd("plc-alias", config));
        Assert.Equal(1, factory.Count);
    }

    [Fact(Timeout = 30000)]
    public void TryRemove_DisposesChannel()
    {
        using var factory = new ChannelFactory(new IChannelCreator[] { new TcpClientChannelCreator() });
        IClientChannel channel = factory.GetOrAdd("plc", TcpTestHelpers.CreateConfig(5000));

        Assert.True(factory.TryRemove("plc"));

        Assert.Equal(ConnectionState.Disposed, channel.State);
        Assert.False(factory.TryGet("plc", out _));
    }

    [Fact(Timeout = 30000)]
    public void Builder_And_DI_ProduceEquivalentFactories()
    {
        TcpClientChannelConfig config = TcpTestHelpers.CreateConfig(5000);

        using ChannelFactory built = ChannelFactoryBuilder.Create().WithCreator(new TcpClientChannelCreator()).Build();

        var services = new ServiceCollection();
        services.AddTcpChannels();
        using ServiceProvider provider = services.BuildServiceProvider();
        ChannelFactory fromContainer = provider.GetRequiredService<ChannelFactory>();

        AssertTcpChannel(built.GetOrAdd("plc", config), "plc", config);
        AssertTcpChannel(fromContainer.GetOrAdd("plc", config), "plc", config);
    }

    [Fact(Timeout = 30000)]
    public void AddTcpChannels_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddTcpChannels();
        services.AddTcpChannels();

        Assert.Single(services.Where(d => d.ServiceType == typeof(IChannelCreator) && d.ImplementationType == typeof(TcpClientChannelCreator)));

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IChannelCreator>());
        Assert.Same(provider.GetRequiredService<ChannelFactory>(), provider.GetRequiredService<ChannelFactory>());
    }

    [Fact(Timeout = 30000)]
    public void TcpCreator_RejectsForeignConfigType()
    {
        var creator = new TcpClientChannelCreator();

        Assert.Throws<ArgumentException>(() => creator.Create("x", new UnregisteredConfig(), null, NullLoggerFactory.Instance));
    }

    private static void AssertTcpChannel(IClientChannel channel, string name, TcpClientChannelConfig config)
    {
        Assert.IsType<TcpClientChannel>(channel);
        Assert.Equal(name, channel.Name);
        var tcp = (ITcpClientChannel)channel;
        Assert.Equal(config.Host, tcp.Config.Host);
        Assert.Equal(config.Port, tcp.Config.Port);
    }

    private sealed class UnregisteredConfig : IChannelConfig
    {
    }

    private sealed class DerivedTcpConfig : TcpClientChannelConfig
    {
    }
}
