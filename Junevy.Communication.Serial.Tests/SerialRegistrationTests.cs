using Junevy.Communication.Channels;
using Junevy.Communication.Serial.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 串口通道的依赖注入注册、创建器与默认命名（与 Tcp 的 <c>ChannelFactoryTests</c> 约定相同）。本类不打开任何端口。
/// </summary>
public sealed class SerialRegistrationTests
{
    [Fact(Timeout = 30000)]
    public void AddSerialChannels_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddSerialChannels();
        services.AddSerialChannels();

        Assert.Single(services.Where(d => d.ServiceType == typeof(IChannelCreator) && d.ImplementationType == typeof(SerialChannelCreator)));

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IChannelCreator>());
        Assert.Same(provider.GetRequiredService<ChannelFactory>(), provider.GetRequiredService<ChannelFactory>());
    }

    [Fact(Timeout = 30000)]
    public void AddSerialChannels_ContainerFactoryCreatesSerialChannel()
    {
        var services = new ServiceCollection();
        services.AddSerialChannels();

        using ServiceProvider provider = services.BuildServiceProvider();
        ChannelFactory factory = provider.GetRequiredService<ChannelFactory>();
        IClientChannel channel = factory.GetOrAdd("COM7", new SerialChannelConfig { PortName = "COM7", BaudRate = 19200 });

        SerialChannel serial = Assert.IsType<SerialChannel>(channel);
        Assert.Equal("COM7", serial.Name);
        Assert.Equal(ConnectionState.Disconnected, serial.State);
    }

    [Fact(Timeout = 30000)]
    public void SerialCreator_RejectsForeignConfigType()
    {
        var creator = new SerialChannelCreator();

        Assert.Equal(typeof(SerialChannelConfig), creator.ConfigType);
        Assert.ThrowsAny<ArgumentException>(() => creator.Create("foreign", new ForeignConfig(), null, NullLoggerFactory.Instance));
    }

    [Fact(Timeout = 30000)]
    public void SerialCreator_UsesNameAndComponents()
    {
        var creator = new SerialChannelCreator();
        var config = new SerialChannelConfig { PortName = "COM8" };

        using IClientChannel channel = creator.Create("slave", config, null, NullLoggerFactory.Instance);

        Assert.IsType<SerialChannel>(channel);
        Assert.Equal("slave", channel.Name);
    }

    [Fact(Timeout = 30000)]
    public void DefaultName_IsPortName()
    {
        using var channel = new SerialChannel(new SerialChannelConfig { PortName = "COM7" });

        Assert.Equal("COM7", channel.Name);
    }

    // 不属于串口族的配置类型，用于验证创建器拒绝外来配置。
    private sealed class ForeignConfig : IChannelConfig
    {
    }
}
