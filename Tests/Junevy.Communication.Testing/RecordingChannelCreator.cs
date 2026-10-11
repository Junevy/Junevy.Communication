using Junevy.Communication.Channels;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Testing;

/// <summary>
/// 记录 <see cref="Create"/> 收到的参数，并返回一个未连接的 <see cref="DuplexClientChannel"/> 的测试用通道创建器。
/// 用于验证协议层的传输绑定：交付方式与介质的传递、组件与名称的传递、被调用的次数。
/// </summary>
public sealed class RecordingChannelCreator : IChannelCreator
{
    /// <summary>创建记录型通道创建器。</summary>
    /// <param name="configType">接受的配置类型。</param>
    /// <param name="delivery">声明的交付方式。</param>
    /// <param name="medium">声明的介质。</param>
    public RecordingChannelCreator(Type configType, ChannelDelivery delivery, ChannelMedium medium)
    {
        ConfigType = configType;
        Delivery = delivery;
        Medium = medium;
    }

    /// <inheritdoc />
    public Type ConfigType { get; }

    /// <inheritdoc />
    public ChannelDelivery Delivery { get; }

    /// <inheritdoc />
    public ChannelMedium Medium { get; }

    /// <summary>被调用的次数。</summary>
    public int CreateCount { get; private set; }

    /// <summary>最近一次收到的通道名称。</summary>
    public string? Name { get; private set; }

    /// <summary>最近一次收到的配置对象。</summary>
    public IChannelConfig? Config { get; private set; }

    /// <summary>最近一次收到的组件。</summary>
    public ChannelComponents? Components { get; private set; }

    /// <summary>最近一次收到的日志工厂。</summary>
    public ILoggerFactory? LoggerFactory { get; private set; }

    /// <inheritdoc />
    public IClientChannel Create(string? name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)
    {
        CreateCount++;
        Name = name;
        Config = config;
        Components = components;
        LoggerFactory = loggerFactory;
        return new DuplexClientChannel(new ClientChannelSettings());
    }
}
