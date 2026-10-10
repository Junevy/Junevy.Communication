using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 服务端的配置（设计文档 7.2）。构造 <see cref="TcpServer"/> 时校验并复制其值（D5）；服务端的运行行为只依赖构造时的快照，
/// 之后修改本对象不影响已创建的服务端。所有超时以毫秒为单位。
/// </summary>
public class TcpServerConfig : IChannelConfig
{
    /// <summary>监听的本地 IP 地址，默认 0.0.0.0（所有 IPv4 地址）。必须是 IP 地址，不接受主机名。</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>监听端口（1–65535）。</summary>
    public int Port { get; set; }

    /// <summary>监听队列长度，必须为正，默认 100。</summary>
    public int Backlog { get; set; } = 100;

    /// <summary>最大会话数；0 表示不限。达到上限后新的连接被立即关闭。</summary>
    public int MaxSessions { get; set; }

    /// <summary>
    /// 允许的远端 IP 地址（白名单）；为 null 或空数组时不限制来源。复杂规则使用 <see cref="IConnectionFilter"/>。
    /// </summary>
    public string[]? AllowedRemoteAddresses { get; set; }

    /// <summary>新会话的握手时限（TLS 与 <c>IConnectionInitializer</c> 的总时间），必须为正，默认 10000。</summary>
    public int SessionHandshakeTimeout { get; set; } = 10000;

    /// <summary>会话连续没有入站数据的最长时间（毫秒），0 表示禁用，超时关闭该会话。</summary>
    public int SessionIdleTimeout { get; set; }

    /// <summary>缓冲区中未成帧数据的最长保留时间（毫秒），0 表示禁用；超时断开该会话。</summary>
    public int PartialFrameTimeout { get; set; }

    /// <summary>单帧写出的超时（毫秒），必须为正，默认 2000。超时后断开该会话。</summary>
    public int SendTimeout { get; set; } = 2000;

    /// <summary>会话发起的请求应答的默认超时（毫秒），必须为正，默认 2000。超时后的处理见 <see cref="ResetOnRequestTimeout"/>。</summary>
    public int RequestTimeout { get; set; } = 2000;

    /// <summary>
    /// 会话发起的请求超时后是否关闭该会话，默认 true（与客户端的默认一致），关闭原因为 <c>RequestTimeout</c>。
    /// 为 false 时超时的请求进入迟到应答窗口：窗口期内继续持有请求锁，窗口内到达的迟到应答被丢弃并计入 <c>FramesDropped</c>。
    /// </summary>
    public bool ResetOnRequestTimeout { get; set; } = true;

    /// <summary>迟到应答窗口（毫秒），仅在 <see cref="ResetOnRequestTimeout"/> 为 false 时生效；-1 表示等于请求超时（默认），0 表示不记录迟到应答。</summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>停止时等待会话关闭的最长时间（毫秒），必须为正，默认 3000；超时后强制中止。</summary>
    public int StopTimeout { get; set; } = 3000;

    /// <summary>分帧配置，默认 Raw。被协议包使用时由 <see cref="TcpChannelComponents"/> 的 FrameCodec 覆盖。</summary>
    public FramingOptions Framing { get; set; } = new FramingOptions();

    /// <summary>关联模式，默认 Sequential。<see cref="ChannelComponents.Correlation"/> 非 null 时被覆盖。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>会话的应用层心跳配置，默认关闭。</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    /// <summary>监听器故障后的重新监听策略（<see cref="ReconnectOptions.Enabled"/> 为 false 时不重新监听，保持 Faulted）。</summary>
    public ReconnectOptions RestartOnFault { get; set; } = new ReconnectOptions();

    /// <summary>套接字选项（无延迟、保活、收发缓冲、Linger），应用于每个接入的连接。</summary>
    public TcpSocketOptions Socket { get; set; } = new TcpSocketOptions();

    /// <summary>TLS 配置，默认关闭（TLS 在后续阶段实现，启用时启动返回 <c>NotSupported</c>）。</summary>
    public TcpServerTlsOptions Tls { get; set; } = new TcpServerTlsOptions();

    /// <summary>每个会话派发队列的容量（帧数），必须为正，默认 1024。队列满时挂起接收，由 TCP 流控把背压传给对端。</summary>
    public int ReceiveQueueCapacity { get; set; } = 1024;
}
