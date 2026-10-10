using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 通道的代码级覆盖。在 <see cref="ChannelComponents"/> 的分帧、关联、握手、心跳与重连覆盖之上，
/// 为 TCP 专属的扩展点预留类型（连接过滤器、证书覆盖等，由后续阶段加入）。
/// </summary>
public class TcpChannelComponents : ChannelComponents
{
}
