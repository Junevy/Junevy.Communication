using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 通道的代码级覆盖。在 <see cref="ChannelComponents"/> 的分帧、关联、握手、心跳与重连覆盖之上，
/// 为 TCP 专属的扩展点提供连接过滤器与按会话创建的心跳探测（服务端使用）；证书覆盖等由后续阶段加入。
/// 服务端不接受 <see cref="ChannelComponents.HealthProbe"/>（它是所有会话共用的一个实例），应改用 <see cref="SessionHealthProbeFactory"/>。
/// </summary>
public class TcpChannelComponents : ChannelComponents
{
    /// <summary>
    /// 连接过滤器：服务端接受新的连接之前调用，返回 false 时立即关闭该连接。仅 <see cref="TcpServer"/> 使用；客户端忽略此项。
    /// </summary>
    public IConnectionFilter? ConnectionFilter { get; set; }

    /// <summary>
    /// 按会话创建心跳探测（仅 <see cref="TcpServer"/> 使用）。启用心跳时，服务端在每个会话加入会话表之后调用一次，返回该会话专用的探测；
    /// 未设置时使用绑定该会话通道的内置负载探测。调用在会话的建立任务中、服务端锁之外进行，实现应当快速返回；
    /// 抛出异常或返回 null 使该会话以 <c>Error</c> 关闭。客户端忽略此项。
    /// </summary>
    public Func<ITcpSession, IHealthProbe>? SessionHealthProbeFactory { get; set; }
}
