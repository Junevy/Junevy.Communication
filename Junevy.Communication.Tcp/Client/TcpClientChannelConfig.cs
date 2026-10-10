using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 客户端通道的配置（设计文档 7.1）。构造 <see cref="TcpClientChannel"/> 时校验并复制其值（D5）；
/// 通道的运行行为只依赖构造时的快照，之后修改本对象不影响已创建的通道。
/// 所有超时以毫秒为单位。
/// </summary>
public class TcpClientChannelConfig : IChannelConfig
{
    /// <summary>远端主机名或 IP 地址，默认 127.0.0.1。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>远端端口（1–65535）。</summary>
    public int Port { get; set; }

    /// <summary>绑定的本地 IP 地址（多网卡工控机常用）；为 null 时由系统选择。</summary>
    public string? LocalAddress { get; set; }

    /// <summary>绑定的本地端口（0–65535）；0 表示由系统分配。</summary>
    public int LocalPort { get; set; }

    /// <summary>单次 TCP 连接的总时限（毫秒），包括域名解析与依次尝试的各地址，必须为正，默认 2000。</summary>
    public int ConnectTimeout { get; set; } = 2000;

    /// <summary>TLS 握手与 <c>IConnectionInitializer</c> 的总时限（毫秒），0 表示不限时，默认 5000。</summary>
    public int HandshakeTimeout { get; set; } = 5000;

    /// <summary>单帧写出的超时（毫秒），必须为正，默认 2000。</summary>
    public int SendTimeout { get; set; } = 2000;

    /// <summary>请求应答的默认超时（毫秒），必须为正，默认 2000。单次请求可以覆盖。</summary>
    public int RequestTimeout { get; set; } = 2000;

    /// <summary>连续没有入站数据的最长时间（毫秒），0 表示禁用。</summary>
    public int IdleTimeout { get; set; }

    /// <summary>缓冲区中未成帧数据的最长保留时间（毫秒），0 表示禁用。</summary>
    public int PartialFrameTimeout { get; set; }

    /// <summary>优雅关闭的等待时间（毫秒），超时后强制关闭，默认 1000。</summary>
    public int DisconnectTimeout { get; set; } = 1000;

    /// <summary>分帧配置，默认 Raw。被协议包使用时由 <see cref="ChannelComponents"/> 覆盖。</summary>
    public FramingOptions Framing { get; set; } = new FramingOptions();

    /// <summary>关联模式，默认 Sequential。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>请求超时后是否断开并重建连接，默认 true；为 false 时进入迟到应答窗口。</summary>
    public bool ResetOnRequestTimeout { get; set; } = true;

    /// <summary>迟到应答窗口（毫秒），仅在 <see cref="ResetOnRequestTimeout"/> 为 false 时生效；-1 表示等于 <see cref="RequestTimeout"/>，默认 -1。</summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>应用层心跳配置。</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    /// <summary>自动重连配置，默认关闭。</summary>
    public ReconnectOptions Reconnect { get; set; } = new ReconnectOptions();

    /// <summary>套接字选项（无延迟、保活、收发缓冲、Linger）。</summary>
    public TcpSocketOptions Socket { get; set; } = new TcpSocketOptions();

    /// <summary>TLS 配置，默认关闭。</summary>
    public TcpClientTlsOptions Tls { get; set; } = new TcpClientTlsOptions();

    /// <summary>派发队列容量（帧数），必须为正，默认 1024。</summary>
    public int ReceiveQueueCapacity { get; set; } = 1024;

    /// <summary>派发队列满时的处理方式，默认 Wait（由 TCP 流控把背压传给对端）。</summary>
    public QueueFullMode QueueFullMode { get; set; } = QueueFullMode.Wait;
}
