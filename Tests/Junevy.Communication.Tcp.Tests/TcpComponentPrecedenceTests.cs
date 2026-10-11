using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 客户端与服务端的分帧配置与协议组件冲突时构造即抛出 <see cref="ArgumentException"/>；默认分帧与组件可以共存（P25-D3）。
/// </summary>
public sealed class TcpComponentPrecedenceTests
{
    private const string FramingMessage =
        "Framing is defined by ChannelComponents.FrameCodec; leave the configuration's Framing at its default.";

    [Fact]
    public void Client_ConfiguredFramingWithAFrameCodec_ThrowsOnConstruction()
    {
        var config = new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = 5000,
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } },
        };
        var components = new TcpChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new TcpClientChannel(config, components: components));

        Assert.StartsWith(FramingMessage, exception.Message);
    }

    [Fact]
    public void Client_DefaultFramingWithAFrameCodec_ConstructsWithoutConnecting()
    {
        var config = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5000 };
        var components = new TcpChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        using var channel = new TcpClientChannel(config, components: components);

        Assert.False(channel.IsConnected);
    }

    [Fact]
    public void Server_ConfiguredFramingWithAFrameCodec_ThrowsOnConstruction()
    {
        var config = new TcpServerConfig
        {
            Port = 5001,
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } },
        };
        var components = new TcpChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new TcpServer(config, components: components));

        Assert.StartsWith(FramingMessage, exception.Message);
    }

    [Fact]
    public void Server_DefaultFramingWithAFrameCodec_Constructs()
    {
        var config = new TcpServerConfig { Port = 5001 };
        var components = new TcpChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        using var server = new TcpServer(config, components: components);

        Assert.NotNull(server);
    }
}
