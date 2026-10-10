using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels;

/// <summary>
/// 按配置类型创建客户端通道的创建器。通道工厂通过 <see cref="ConfigType"/> 精确匹配选择创建器。
/// </summary>
public interface IChannelCreator
{
    /// <summary>此创建器接受的配置类型（精确匹配）。</summary>
    Type ConfigType { get; }

    /// <summary>
    /// 创建通道。实现内部必须校验配置（非法时抛出 <see cref="ArgumentException"/>），且不得修改调用方的配置对象。
    /// </summary>
    /// <param name="name">通道名称。</param>
    /// <param name="config">配置对象，类型必须为 <see cref="ConfigType"/>。</param>
    /// <param name="components">协议包的代码级覆盖；为 null 时只使用配置。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>新建的客户端通道。</returns>
    IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory);
}
