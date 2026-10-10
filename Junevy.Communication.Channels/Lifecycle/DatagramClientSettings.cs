namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 数据报客户端通道的运行参数（内部）。由传输配置（UDP）映射而来，<see cref="DatagramClientOptions"/> 构造时校验并复制（D5）。
/// 超时以毫秒为单位，0 表示不限时（HandshakeTimeout 与 DisconnectTimeout 的 0 表示不限时；IdleTimeout 的 0 表示禁用）。
/// </summary>
internal sealed class DatagramClientSettings
{
    /// <summary>握手时限（毫秒）：打开传输之后的初始化器总时限。</summary>
    public int HandshakeTimeout { get; set; } = 5000;

    /// <summary>单个数据报写出的超时（毫秒）。</summary>
    public int SendTimeout { get; set; } = 2000;

    /// <summary>请求应答的默认超时（毫秒），每次尝试单独计时。</summary>
    public int RequestTimeout { get; set; } = 2000;

    /// <summary>请求超时后原样重发的次数。</summary>
    public int RequestRetryCount { get; set; }

    /// <summary>迟到应答窗口（毫秒）：-1 表示等于请求超时。</summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>空闲超时（毫秒）：连续没有入站数据报的最长时间；0 表示禁用。</summary>
    public int IdleTimeout { get; set; }

    /// <summary>优雅关闭时排空已收到帧的时限（毫秒）。</summary>
    public int DisconnectTimeout { get; set; } = 1000;

    /// <summary>数据报的最大负载（字节），范围 [1, 65507]。</summary>
    public int MaxDatagramSize { get; set; } = 65507;

    /// <summary>派发队列容量（帧数）。</summary>
    public int ReceiveQueueCapacity { get; set; } = 1024;

    /// <summary>派发队列满时的处理方式。</summary>
    public QueueFullMode QueueFullMode { get; set; } = QueueFullMode.DropOldest;

    /// <summary>关联模式。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>应用层心跳配置。</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    /// <summary>自动重连配置。</summary>
    public ReconnectOptions Reconnect { get; set; } = new ReconnectOptions();
}
