namespace Junevy.Communication.Channels;

/// <summary>
/// 字节流客户端通道（<c>StreamClientChannel</c>）的运行参数。派生类把自己的配置映射到这里。
/// 通道构造时校验并复制（D5）：非法值抛出 <see cref="ArgumentException"/> 族，调用方的对象不会被修改。
/// 所有超时以毫秒为单位，0 表示不限时（<see cref="IdleTimeout"/> 与 <see cref="PartialFrameTimeout"/> 的 0 表示禁用）。
/// </summary>
/// <remarks>
/// 超时类默认值与 TCP 客户端配置（<c>TcpClientChannelConfig</c>）一致（计划 Task 14 审阅结论）：
/// 派生通道遗漏赋值时仍有超时保护，而不是"请求永不超时"。传输的 BuildSettings 仍应显式赋值，不依赖这里的默认值。
/// </remarks>
public sealed class ClientChannelSettings
{
    /// <summary>握手超时（毫秒）：<see cref="IConnectionInitializer"/> 与 TLS 包装共享的总时限；默认 5000，0 表示不限时。</summary>
    public int HandshakeTimeout { get; set; } = 5000;

    /// <summary>单帧写出超时（毫秒）；默认 2000，0 表示不限时。</summary>
    public int SendTimeout { get; set; } = 2000;

    /// <summary>请求应答超时（毫秒）；默认 2000，0 表示不限时。单次请求可以用 <see cref="RequestOptions.Timeout"/> 覆盖。</summary>
    public int RequestTimeout { get; set; } = 2000;

    /// <summary>迟到应答窗口（毫秒）：默认 -1 表示等于各请求自身的超时；0 表示不记录迟到应答。</summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>空闲超时（毫秒）：连续没有入站帧的最长时间，超时即断开并重连；默认 0（禁用）。</summary>
    public int IdleTimeout { get; set; }

    /// <summary>未成帧数据的最长保留时间（毫秒）；默认 0（禁用）。超时后的处理方式由派生类的 PartialFrameAction 决定。</summary>
    public int PartialFrameTimeout { get; set; }

    /// <summary>优雅关闭的等待时间（毫秒），超时后强制关闭；默认 1000，0 表示不等待。</summary>
    public int DisconnectTimeout { get; set; } = 1000;

    /// <summary>每次从传输读取时申请的缓冲大小（字节），必须为正；默认 4096。</summary>
    public int ReceiveBufferSize { get; set; } = 4096;

    /// <summary>分帧配置；<see cref="ChannelComponents.FrameCodec"/> 非 null 时被覆盖。</summary>
    public FramingOptions Framing { get; set; } = new FramingOptions();

    /// <summary>关联模式；<see cref="ChannelComponents.Correlation"/> 非 null 时被覆盖。默认 <see cref="CorrelationMode.Sequential"/>。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>
    /// 请求超时后是否断开并重建连接。默认 false：超时后保留连接，并按迟到应答窗口处理。
    /// TCP 客户端配置的默认值为 true。需要"超时即重建"语义的派生传输应显式设置，串口与 UDP 不使用此项。
    /// </summary>
    public bool ResetOnRequestTimeout { get; set; }

    /// <summary>应用层心跳配置。</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    /// <summary>自动重连配置。</summary>
    public ReconnectOptions Reconnect { get; set; } = new ReconnectOptions();

    /// <summary>派发队列容量（帧数），必须为正；默认 1024。</summary>
    public int ReceiveQueueCapacity { get; set; } = 1024;

    /// <summary>派发队列满时的处理方式；默认 <see cref="QueueFullMode.Wait"/>。</summary>
    public QueueFullMode QueueFullMode { get; set; } = QueueFullMode.Wait;
}
