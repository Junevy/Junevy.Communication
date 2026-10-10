using Junevy.Communication.Channels;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Serial;

/// <summary>
/// <see cref="SerialChannelConfig"/> 的通道创建器，由 <see cref="ChannelFactory"/> 按配置的精确类型选择。
/// </summary>
public sealed class SerialChannelCreator : IChannelCreator
{
    /// <inheritdoc />
    public Type ConfigType => typeof(SerialChannelConfig);

    /// <summary>
    /// 创建串口通道（尚未打开端口）。配置的校验由 <see cref="SerialChannel"/> 的构造函数完成（D5），本方法不修改调用方的配置对象。
    /// </summary>
    /// <param name="name">通道名称。</param>
    /// <param name="config">必须是 <see cref="SerialChannelConfig"/>。</param>
    /// <param name="components">代码级覆盖；为 null 时只使用配置。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>新建的 <see cref="SerialChannel"/>。</returns>
    /// <exception cref="ArgumentNullException">配置或日志工厂为 null。</exception>
    /// <exception cref="ArgumentException">配置不是 <see cref="SerialChannelConfig"/>，或配置非法。</exception>
    public IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (loggerFactory == null)
            throw new ArgumentNullException(nameof(loggerFactory));
        if (config is not SerialChannelConfig serialConfig)
            throw new ArgumentException(
                $"SerialChannelCreator requires '{typeof(SerialChannelConfig).FullName}' but received '{config.GetType().FullName}'.",
                nameof(config));

        return new SerialChannel(name, serialConfig, loggerFactory.CreateLogger<SerialChannel>(), components);
    }
}
