using Junevy.Communication.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// <see cref="TcpClientChannelCreator"/> 的传输特征（字节流、以太网）与名称为 null 时的默认名称（P25-D4、D5）。
/// </summary>
public sealed class TcpClientChannelCreatorTests
{
    [Fact]
    public void DeclaresStreamDeliveryAndNetworkMedium()
    {
        var creator = new TcpClientChannelCreator();

        Assert.Equal(ChannelDelivery.Stream, creator.Delivery);
        Assert.Equal(ChannelMedium.Network, creator.Medium);
    }

    [Fact]
    public void NullName_UsesTheSameNameAsTheUnnamedConstructor()
    {
        var config = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5000 };
        var creator = new TcpClientChannelCreator();

        using IClientChannel named = creator.Create(null, config, null, NullLoggerFactory.Instance);
        using var unnamed = new TcpClientChannel(config);

        Assert.Equal(unnamed.Name, named.Name);
    }
}
