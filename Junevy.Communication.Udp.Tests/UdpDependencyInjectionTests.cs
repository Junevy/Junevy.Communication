using Junevy.Communication.Channels;
using Junevy.Communication.Udp.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Udp.Tests;

/// <summary>依赖注入与通道创建器的测试。</summary>
public sealed class UdpDependencyInjectionTests
{
    [Fact(Timeout = 30000)]
    public void AddUdpChannels_IsIdempotent_RegistersOneCreator()
    {
        var services = new ServiceCollection();
        services.AddUdpChannels().AddUdpChannels();
        using ServiceProvider provider = services.BuildServiceProvider();

        var creators = provider.GetServices<IChannelCreator>().OfType<UdpChannelCreator>().ToList();

        Assert.Single(creators);
        Assert.NotNull(provider.GetRequiredService<ChannelFactory>());
    }

    [Fact(Timeout = 30000)]
    public void ChannelFactory_CreatesUdpChannel_ForUdpConfig()
    {
        var services = new ServiceCollection();
        services.AddUdpChannels();
        using ServiceProvider provider = services.BuildServiceProvider();
        ChannelFactory factory = provider.GetRequiredService<ChannelFactory>();

        IClientChannel channel = factory.GetOrAdd("udp-one", new UdpChannelConfig { LocalAddress = "127.0.0.1" });

        Assert.IsType<UdpChannel>(channel);
        Assert.Equal("udp-one", channel.Name);
    }

    [Fact(Timeout = 30000)]
    public void Creator_RejectsForeignConfig()
    {
        var creator = new UdpChannelCreator();

        Assert.Throws<ArgumentException>(() => creator.Create("foreign", new ForeignConfig(), null, NullLoggerFactory.Instance));
    }

    [Fact(Timeout = 30000)]
    public void Creator_UsesRequestedName()
    {
        var creator = new UdpChannelCreator();

        using IClientChannel channel = creator.Create("named", new UdpChannelConfig { LocalAddress = "127.0.0.1" }, null, NullLoggerFactory.Instance);

        Assert.Equal("named", channel.Name);
        Assert.Equal(typeof(UdpChannelConfig), creator.ConfigType);
    }

    private sealed class ForeignConfig : IChannelConfig
    {
    }
}
