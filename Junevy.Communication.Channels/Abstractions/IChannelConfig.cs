namespace Junevy.Communication.Channels;

/// <summary>
/// 通道配置的标记接口。每种通道有自己的配置类型（例如 TcpClientChannelConfig），
/// 由对应的 <see cref="IChannelCreator"/> 通过 <see cref="IChannelCreator.ConfigType"/> 精确匹配识别。
/// </summary>
public interface IChannelConfig
{
}
