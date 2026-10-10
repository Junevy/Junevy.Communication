using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc;

/// <summary>
/// 按配置类型创建协议客户端的创建器。<see cref="PlcClientFactory"/> 通过 <see cref="ConfigType"/> 精确匹配选择创建器。
/// </summary>
public interface IPlcClientCreator
{
    /// <summary>此创建器接受的配置类型（精确匹配）。</summary>
    Type ConfigType { get; }

    /// <summary>
    /// 创建客户端。实现内部必须校验配置（非法时抛出 <see cref="ArgumentException"/>），且不得修改调用方的配置对象。
    /// </summary>
    /// <param name="name">客户端名称。</param>
    /// <param name="config">配置对象，类型必须为 <see cref="ConfigType"/>。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>新建的客户端。</returns>
    IPlcClient Create(string name, IPlcClientConfig config, ILoggerFactory loggerFactory);
}
