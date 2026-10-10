using Junevy.Communication.Channels;
using Junevy.Communication.Serial.Internal;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 测试用的通道创建器：与 <see cref="SerialChannelCreator"/> 相同的配置类型，但通道使用假端口工厂。
/// 用于通过 <see cref="ChannelFactory"/> 与别名验证共享通道，而不打开真实串口。
/// </summary>
internal sealed class FakeSerialChannelCreator : IChannelCreator
{
    private readonly ISerialPortHandleFactory handleFactory;

    /// <summary>创建测试用创建器。</summary>
    /// <param name="handleFactory">假端口工厂。</param>
    public FakeSerialChannelCreator(ISerialPortHandleFactory handleFactory)
    {
        this.handleFactory = handleFactory;
    }

    /// <inheritdoc />
    public Type ConfigType => typeof(SerialChannelConfig);

    /// <inheritdoc />
    public IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)
        => new SerialChannel(name, (SerialChannelConfig)config, loggerFactory.CreateLogger<SerialChannel>(), components, handleFactory);
}
