using Junevy.Communication.Channels;

namespace Junevy.Communication.Udp.Tests;

/// <summary>
/// UDP 的关联配置与协议组件冲突时构造即抛出 <see cref="ArgumentException"/>；默认配置与组件可以共存（P25-D3）。
/// </summary>
public sealed class UdpComponentPrecedenceTests
{
    private const string CorrelationMessage =
        "Correlation is defined by ChannelComponents.Correlation; leave the configuration's Correlation at its default or set it to the same value.";

    [Fact]
    public void ConfiguredCorrelationDifferentFromTheComponent_ThrowsOnConstruction()
    {
        var config = new UdpChannelConfig { Correlation = CorrelationMode.Keyed };
        var components = new ChannelComponents { Correlation = CorrelationMode.Sequential };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new UdpChannel(config, components: components));

        Assert.StartsWith(CorrelationMessage, exception.Message);
    }

    [Fact]
    public void DefaultCorrelationWithAComponent_Constructs()
    {
        var config = new UdpChannelConfig();
        var components = new ChannelComponents { Correlation = CorrelationMode.Sequential };

        var channel = new UdpChannel(config, components: components);

        Assert.False(channel.IsConnected);
    }
}
