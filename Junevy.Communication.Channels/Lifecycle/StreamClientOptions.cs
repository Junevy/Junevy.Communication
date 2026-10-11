using Junevy.Communication.Channels.Pipeline;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 字节流客户端通道的已校验运行参数（D5）。构造时从 <see cref="ClientChannelSettings"/> 与 <see cref="ChannelComponents"/> 解析并复制，
/// 之后不再读取调用方的对象；<see cref="ChannelComponents"/> 中的同类设置优先，重连策略仍受 <see cref="ReconnectOptions.Enabled"/> 控制。
/// 公共字段由 <see cref="Core"/> 校验与保存；本类只保留字节流独有的设置。
/// </summary>
internal sealed class StreamClientOptions
{
    /// <summary>
    /// 校验并解析配置。
    /// </summary>
    /// <param name="settings">通道配置；不能为 null。</param>
    /// <param name="components">代码级覆盖；可为 null。</param>
    /// <param name="probeChannel">内置心跳探测执行请求的字节通道。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="ArgumentException">配置非法（例如启用心跳却没有探测的负载）。</exception>
    public StreamClientOptions(ClientChannelSettings settings, ChannelComponents? components, IByteChannel probeChannel)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (probeChannel == null)
            throw new ArgumentNullException(nameof(probeChannel));
        if (settings.Framing == null)
            throw new ArgumentException("Framing must not be null.", nameof(settings));

        Core = new ClientOptionsCore(ToCoreSettings(settings), components, probeChannel, nameof(ClientChannelSettings.Heartbeat));
        Codec = Core.Codec!;
        PartialFrameTimeout = settings.PartialFrameTimeout;
        ReceiveBufferSize = settings.ReceiveBufferSize;
        ResetOnRequestTimeout = settings.ResetOnRequestTimeout;
    }

    /// <summary>公共运行参数（已校验）。</summary>
    public ClientOptionsCore Core { get; }

    /// <summary>分帧编解码工厂（已应用 <see cref="ChannelComponents.FrameCodec"/> 的覆盖）。</summary>
    public IFrameCodecFactory Codec { get; }

    /// <summary>未成帧数据的最长保留时间（毫秒），0 表示禁用。</summary>
    public int PartialFrameTimeout { get; }

    /// <summary>接收缓冲区的单次申请大小（字节）。</summary>
    public int ReceiveBufferSize { get; }

    /// <summary>请求超时后是否重建连接。</summary>
    public bool ResetOnRequestTimeout { get; }

    /// <summary>
    /// 为一次连接创建 <see cref="StreamChannel"/> 的运行参数。
    /// </summary>
    /// <param name="partialFrameAction">半帧超时的处理方式（派生类的 PartialFrameAction）。</param>
    /// <returns>新的运行参数对象。</returns>
    public StreamChannelSettings CreateStreamSettings(PartialFrameAction partialFrameAction)
        => new StreamChannelSettings
        {
            SendTimeout = Core.SendTimeout,
            RequestTimeout = Core.RequestTimeout,
            LateReplyWindow = Core.LateReplyWindow,
            PartialFrameAction = partialFrameAction,
            PartialFrameTimeout = PartialFrameTimeout,
            ResetOnRequestTimeout = ResetOnRequestTimeout,
            Correlation = Core.Correlation,
            ReceiveBufferSize = ReceiveBufferSize,
        };

    private static ClientCoreSettings ToCoreSettings(ClientChannelSettings settings)
        => new ClientCoreSettings
        {
            HandshakeTimeout = settings.HandshakeTimeout,
            SendTimeout = settings.SendTimeout,
            RequestTimeout = settings.RequestTimeout,
            LateReplyWindow = settings.LateReplyWindow,
            IdleTimeout = settings.IdleTimeout,
            DisconnectTimeout = settings.DisconnectTimeout,
            ReceiveQueueCapacity = settings.ReceiveQueueCapacity,
            QueueFullMode = settings.QueueFullMode,
            Correlation = settings.Correlation,
            Heartbeat = settings.Heartbeat,
            Reconnect = settings.Reconnect,
            PartialFrameTimeout = settings.PartialFrameTimeout,
            ReceiveBufferSize = settings.ReceiveBufferSize,
            Framing = settings.Framing,
        };
}
