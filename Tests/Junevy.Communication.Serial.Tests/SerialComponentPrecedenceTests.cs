using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 串口的分帧配置与协议组件冲突时构造即抛出 <see cref="ArgumentException"/>；默认分帧与组件可以共存。
/// 构造函数不打开端口，因此这些测试使用不存在的端口名 COM20 也不会访问硬件（P25-D3）。
/// </summary>
public sealed class SerialComponentPrecedenceTests
{
    private const string FramingMessage =
        "Framing is defined by ChannelComponents.FrameCodec; leave the configuration's Framing at its default.";

    [Fact]
    public void ConfiguredFramingWithAFrameCodec_ThrowsOnConstruction()
    {
        var config = new SerialChannelConfig
        {
            PortName = "COM20",
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } },
        };
        var components = new ChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new SerialChannel(config, components: components));

        Assert.StartsWith(FramingMessage, exception.Message);
    }

    [Fact]
    public void DefaultFramingWithAFrameCodec_ConstructsWithoutOpeningThePort()
    {
        var config = new SerialChannelConfig { PortName = "COM20" };
        var components = new ChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        using var channel = new SerialChannel(config, components: components);

        Assert.False(channel.IsConnected);
    }
}
