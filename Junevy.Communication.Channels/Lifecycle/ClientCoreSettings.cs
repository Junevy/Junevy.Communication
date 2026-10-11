namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 字节流与数据报客户端共用的运行参数（内部）。两种传输的配置映射到这里，由 <see cref="ClientOptionsCore"/> 一次性校验（D5）。
/// 属性没有默认值：映射方（<c>StreamClientOptions</c> 与 <c>UdpChannelSettings</c>）负责设置属于它的每一个成员；
/// 心跳与重连为 null 时 <see cref="ClientOptionsCore"/> 报错，因此遗漏的映射不会静默地使用错误的值。
/// 只属于一种传输的设置为可空：另一种传输保持 null，表示不使用该设置（数据报没有分帧，字节流没有重试次数）。
/// 超时以毫秒为单位，0 表示不限时（HandshakeTimeout 与 DisconnectTimeout 的 0 表示不限时；IdleTimeout 与 PartialFrameTimeout 的 0 表示禁用）。
/// </summary>
internal sealed class ClientCoreSettings
{
    /// <summary>握手时限（毫秒）：打开传输之后的初始化器总时限。</summary>
    public int HandshakeTimeout { get; set; }

    /// <summary>单帧或单个数据报写出的超时（毫秒）。</summary>
    public int SendTimeout { get; set; }

    /// <summary>请求应答的默认超时（毫秒）。</summary>
    public int RequestTimeout { get; set; }

    /// <summary>迟到应答窗口（毫秒）：-1 表示等于请求超时。</summary>
    public int LateReplyWindow { get; set; }

    /// <summary>空闲超时（毫秒）：连续没有入站数据的最长时间；0 表示禁用。</summary>
    public int IdleTimeout { get; set; }

    /// <summary>优雅关闭的等待时间（毫秒）。</summary>
    public int DisconnectTimeout { get; set; }

    /// <summary>派发队列容量（帧数）。</summary>
    public int ReceiveQueueCapacity { get; set; }

    /// <summary>派发队列满时的处理方式。</summary>
    public QueueFullMode QueueFullMode { get; set; }

    /// <summary>关联模式。</summary>
    public CorrelationMode Correlation { get; set; }

    /// <summary>应用层心跳配置（映射方必须设置）。</summary>
    public HeartbeatOptions? Heartbeat { get; set; }

    /// <summary>自动重连配置（映射方必须设置）。</summary>
    public ReconnectOptions? Reconnect { get; set; }

    /// <summary>请求超时后原样重发的次数（仅数据报使用）。</summary>
    public int? RequestRetryCount { get; set; }

    /// <summary>数据报的最大负载（字节），范围 [1, 65507]（仅数据报使用）。</summary>
    public int? MaxDatagramSize { get; set; }

    /// <summary>未成帧数据的最长保留时间（毫秒），0 表示禁用（仅字节流使用）。</summary>
    public int? PartialFrameTimeout { get; set; }

    /// <summary>接收缓冲区的单次申请大小（字节）（仅字节流使用）。</summary>
    public int? ReceiveBufferSize { get; set; }

    /// <summary>分帧配置（仅字节流使用）。</summary>
    public FramingOptions? Framing { get; set; }
}
