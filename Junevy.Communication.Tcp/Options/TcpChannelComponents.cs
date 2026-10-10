using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 通道的代码级覆盖。在 <see cref="ChannelComponents"/> 的分帧、关联、握手、心跳与重连覆盖之上，
/// 为 TCP 专属的扩展点提供连接过滤器、按会话创建的心跳探测与 TLS 证书覆盖（设计文档 7.3）。
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

    /// <summary>
    /// 客户端证书（双向认证）。优先于 <see cref="TcpClientTlsOptions.ClientCertificate"/>。证书由调用方持有，通道不释放它。仅客户端使用。
    /// 必须带私钥。
    /// </summary>
    public X509Certificate2? ClientCertificate { get; set; }

    /// <summary>
    /// 服务端证书。优先于 <see cref="TcpServerTlsOptions.ServerCertificate"/>。证书由调用方持有，服务端释放时不释放它。仅服务端使用。
    /// 必须带私钥。
    /// </summary>
    public X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// 远端证书校验回调：客户端用它校验服务端证书，服务端用它校验客户端证书。设置后以回调的结果为准，
    /// 忽略 <see cref="TcpClientTlsOptions.AllowUntrustedServerCertificate"/> 与默认的校验规则。
    /// </summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; set; }
}
