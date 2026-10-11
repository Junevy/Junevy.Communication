using Junevy.Communication.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// <see cref="SerialChannelCreator"/> 的传输特征（字节流、串口）与名称为 null 时的默认名称（P25-D4、D5）。
/// 构造通道不打开端口，因此使用端口名 COM20 也不会访问硬件。
/// </summary>
public sealed class SerialChannelCreatorTests
{
    [Fact]
    public void DeclaresStreamDeliveryAndSerialMedium()
    {
        var creator = new SerialChannelCreator();

        Assert.Equal(ChannelDelivery.Stream, creator.Delivery);
        Assert.Equal(ChannelMedium.Serial, creator.Medium);
    }

    [Fact]
    public void NullName_UsesTheSameNameAsTheUnnamedConstructor()
    {
        var config = new SerialChannelConfig { PortName = "COM20" };
        var creator = new SerialChannelCreator();

        using IClientChannel named = creator.Create(null, config, null, NullLoggerFactory.Instance);
        using var unnamed = new SerialChannel(config);

        Assert.Equal(unnamed.Name, named.Name);
    }
}
