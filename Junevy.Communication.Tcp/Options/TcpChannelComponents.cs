using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 通道的代码级覆盖。在 <see cref="ChannelComponents"/> 的分帧、关联、握手、心跳与重连覆盖之上，
/// 为 TCP 专属的扩展点提供连接过滤器（仅 <see cref="TcpServer"/> 使用）；证书覆盖等由后续阶段加入。
/// </summary>
public class TcpChannelComponents : ChannelComponents
{
    /// <summary>
    /// 连接过滤器：服务端接受新的连接之前调用，返回 false 时立即关闭该连接。仅 <see cref="TcpServer"/> 使用；客户端忽略此项。
    /// </summary>
    public IConnectionFilter? ConnectionFilter { get; set; }
}
