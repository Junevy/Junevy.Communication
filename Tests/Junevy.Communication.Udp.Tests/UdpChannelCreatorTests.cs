using Junevy.Communication.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Udp.Tests;

/// <summary>
/// <see cref="UdpChannelCreator"/> 的传输特征（数据报、以太网）与名称为 null 时的默认名称（P25-D4、D5）。
/// </summary>
public sealed class UdpChannelCreatorTests
{
    [Fact]
    public void DeclaresDatagramDeliveryAndNetworkMedium()
    {
        var creator = new UdpChannelCreator();

        Assert.Equal(ChannelDelivery.Datagram, creator.Delivery);
        Assert.Equal(ChannelMedium.Network, creator.Medium);
    }

    [Fact]
    public void NullName_UsesTheSameNameAsTheUnnamedConstructor()
    {
        var config = new UdpChannelConfig { LocalAddress = "127.0.0.1", LocalPort = 0 };
        var creator = new UdpChannelCreator();

        using IClientChannel named = creator.Create(null, config, null, NullLoggerFactory.Instance);
        using var unnamed = new UdpChannel(config);

        Assert.Equal(unnamed.Name, named.Name);
    }
}
