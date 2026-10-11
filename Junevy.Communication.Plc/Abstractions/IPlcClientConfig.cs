using Junevy.Communication.Core.Registry;

namespace Junevy.Communication.Plc;

/// <summary>
/// 协议客户端配置的标记接口。每种协议有自己的配置类型（例如 MELSEC 的 <c>McClientConfig</c>），
/// 由对应的 <see cref="IPlcClientCreator"/> 通过 <see cref="IConfigTypedCreator.ConfigType"/> 精确匹配识别。
/// </summary>
public interface IPlcClientConfig
{
}
