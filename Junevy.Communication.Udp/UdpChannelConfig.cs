using Junevy.Communication.Channels;

namespace Junevy.Communication.Udp;

/// <summary>
/// UDP 通道的配置（设计文档第 9 节，D11 增加 <see cref="MulticastLoopback"/>，计划 15.1）。构造 <see cref="UdpChannel"/> 时校验并复制其值（D5）；
/// 通道的运行行为只依赖构造时的快照，之后修改本对象不影响已创建的通道。所有超时以毫秒为单位。
/// </summary>
public class UdpChannelConfig : IChannelConfig
{
    /// <summary>本地绑定地址（IP 字面量），默认 0.0.0.0。</summary>
    public string LocalAddress { get; set; } = "0.0.0.0";

    /// <summary>本地绑定端口（0–65535）；0 表示由系统分配。</summary>
    public int LocalPort { get; set; }

    /// <summary>远端主机名或 IP 地址；设置后为定向模式。必须与 <see cref="RemotePort"/> 同时设置或同时不设置。</summary>
    public string? RemoteHost { get; set; }

    /// <summary>远端端口（1–65535）；定向模式下必须设置。</summary>
    public int RemotePort { get; set; }

    /// <summary>是否允许广播（SO_BROADCAST），默认 false。</summary>
    public bool EnableBroadcast { get; set; }

    /// <summary>需要加入的组播地址（IP 字面量，必须是组播地址）；为 null 时不加入任何组播组。</summary>
    public string[]? MulticastGroups { get; set; }

    /// <summary>组播的 TTL（0–255），默认 1。</summary>
    public int MulticastTimeToLive { get; set; } = 1;

    /// <summary>组播数据是否回环到本机（IP_MULTICAST_LOOP），默认 false。</summary>
    public bool MulticastLoopback { get; set; }

    /// <summary>绑定前是否设置 SO_REUSEADDR，默认 false。</summary>
    public bool ReuseAddress { get; set; }

    /// <summary>套接字接收缓冲（SO_RCVBUF，字节），必须为正，默认 65536。</summary>
    public int ReceiveBufferSize { get; set; } = 65536;

    /// <summary>数据报的最大负载（字节），范围 [1, 65507]，默认 65507。超过的数据报丢弃并计为协议错误。</summary>
    public int MaxDatagramSize { get; set; } = 65507;

    /// <summary>握手时限（毫秒）：<c>IConnectionInitializer</c> 的总时限，必须为正，默认 5000。</summary>
    public int HandshakeTimeout { get; set; } = 5000;

    /// <summary>优雅断开时排空已收到但未派发的帧的时限（毫秒），默认 1000。</summary>
    public int DisconnectTimeout { get; set; } = 1000;

    /// <summary>单个数据报写出的超时（毫秒），必须为正，默认 2000。</summary>
    public int SendTimeout { get; set; } = 2000;

    /// <summary>请求应答的默认超时（毫秒），必须为正，默认 2000。每次尝试单独计时。</summary>
    public int RequestTimeout { get; set; } = 2000;

    /// <summary>请求超时后原样重发的次数（≥ 0），默认 0。UDP 不保证送达，重发用于掩盖单次丢包。</summary>
    public int RequestRetryCount { get; set; }

    /// <summary>连续没有入站数据报的最长时间（毫秒），0 表示禁用。</summary>
    public int IdleTimeout { get; set; }

    /// <summary>迟到应答窗口（毫秒）：-1 表示等于 <see cref="RequestTimeout"/>，0 表示不记录迟到应答，默认 -1。</summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>关联模式，默认 Sequential。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>应用层心跳配置。定向模式下内置探测发往远端；非定向模式需要 <c>ChannelComponents.HealthProbe</c>。</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    /// <summary>自动重连配置（重连即重新绑定套接字），默认关闭。</summary>
    public ReconnectOptions Reconnect { get; set; } = new ReconnectOptions();

    /// <summary>派发队列容量（帧数），必须为正，默认 1024。</summary>
    public int ReceiveQueueCapacity { get; set; } = 1024;

    /// <summary>派发队列满时的处理方式，默认 DropOldest（UDP 没有流控，设计文档 6.4 节）。</summary>
    public QueueFullMode QueueFullMode { get; set; } = QueueFullMode.DropOldest;
}
