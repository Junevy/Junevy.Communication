using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 字节流客户端通道的已校验运行参数（D5）。构造时从 <see cref="ClientChannelSettings"/> 与 <see cref="ChannelComponents"/> 解析并复制，
/// 之后不再读取调用方的对象；<see cref="ChannelComponents"/> 中的同类设置优先，重连策略仍受 <see cref="ReconnectOptions.Enabled"/> 控制。
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
        if (settings.Heartbeat == null)
            throw new ArgumentException("Heartbeat must not be null.", nameof(settings));
        if (settings.Reconnect == null)
            throw new ArgumentException("Reconnect must not be null.", nameof(settings));
        LifecycleSupport.RequireSingleHealthProbe(components);

        HandshakeTimeout = NonNegative(settings.HandshakeTimeout, nameof(ClientChannelSettings.HandshakeTimeout));
        SendTimeout = NonNegative(settings.SendTimeout, nameof(ClientChannelSettings.SendTimeout));
        RequestTimeout = NonNegative(settings.RequestTimeout, nameof(ClientChannelSettings.RequestTimeout));
        IdleTimeout = NonNegative(settings.IdleTimeout, nameof(ClientChannelSettings.IdleTimeout));
        PartialFrameTimeout = NonNegative(settings.PartialFrameTimeout, nameof(ClientChannelSettings.PartialFrameTimeout));
        DisconnectTimeout = NonNegative(settings.DisconnectTimeout, nameof(ClientChannelSettings.DisconnectTimeout));
        if (settings.LateReplyWindow < -1)
            throw new ArgumentOutOfRangeException(nameof(ClientChannelSettings.LateReplyWindow), settings.LateReplyWindow,
                "The late reply window must be -1 or a non-negative value.");
        LateReplyWindow = settings.LateReplyWindow;
        ReceiveBufferSize = Positive(settings.ReceiveBufferSize, nameof(ClientChannelSettings.ReceiveBufferSize));
        QueueCapacity = Positive(settings.ReceiveQueueCapacity, nameof(ClientChannelSettings.ReceiveQueueCapacity));
        if (!Enum.IsDefined(typeof(QueueFullMode), settings.QueueFullMode))
            throw new ArgumentOutOfRangeException(nameof(ClientChannelSettings.QueueFullMode), settings.QueueFullMode, "Unknown queue full mode.");
        QueueFullMode = settings.QueueFullMode;
        ResetOnRequestTimeout = settings.ResetOnRequestTimeout;

        CorrelationMode correlation = components?.Correlation ?? settings.Correlation;
        if (!Enum.IsDefined(typeof(CorrelationMode), correlation))
            throw new ArgumentOutOfRangeException(nameof(ClientChannelSettings.Correlation), correlation, "Unknown correlation mode.");
        Correlation = correlation;
        KeyExtractor = components?.KeyExtractor;
        if (correlation == CorrelationMode.Keyed && KeyExtractor == null)
            throw new ArgumentException("Keyed correlation requires ChannelComponents.KeyExtractor.", nameof(components));

        Codec = components?.FrameCodec ?? FrameCodecFactory.Create(settings.Framing);
        Initializer = components?.Initializer;

        Heartbeat = CopyHeartbeat(settings.Heartbeat);
        if (Heartbeat.Enabled)
        {
            // 探测的来源：代码级探测、代码级工厂（由驱动在第一次打开时调用）或内置负载探测。前两者存在时不需要心跳负载。
            HealthProbe = components?.HealthProbe;
            HealthProbeFactory = components?.HealthProbeFactory;
            if (HealthProbe == null && HealthProbeFactory == null)
                HealthProbe = CreatePayloadProbe(Heartbeat, probeChannel);
        }
        else
        {
            HealthProbe = null;
            HealthProbeFactory = null;
        }

        ReconnectPolicy = settings.Reconnect.Enabled
            ? components?.ReconnectPolicy ?? BackoffPolicyFactory.Create(settings.Reconnect)
            : null;
        ReconnectOnInitialFailure = settings.Reconnect.Enabled && settings.Reconnect.OnInitialFailure;
    }

    /// <summary>握手超时（毫秒），0 表示不限时。</summary>
    public int HandshakeTimeout { get; }

    /// <summary>单帧写出超时（毫秒），0 表示不限时。</summary>
    public int SendTimeout { get; }

    /// <summary>请求应答超时（毫秒），0 表示不限时。</summary>
    public int RequestTimeout { get; }

    /// <summary>迟到应答窗口（毫秒），-1 表示等于请求超时。</summary>
    public int LateReplyWindow { get; }

    /// <summary>空闲超时（毫秒），0 表示禁用。</summary>
    public int IdleTimeout { get; }

    /// <summary>未成帧数据的最长保留时间（毫秒），0 表示禁用。</summary>
    public int PartialFrameTimeout { get; }

    /// <summary>优雅关闭的等待时间（毫秒）。</summary>
    public int DisconnectTimeout { get; }

    /// <summary>接收缓冲区的单次申请大小（字节）。</summary>
    public int ReceiveBufferSize { get; }

    /// <summary>请求超时后是否重建连接。</summary>
    public bool ResetOnRequestTimeout { get; }

    /// <summary>派发队列容量（帧数）。</summary>
    public int QueueCapacity { get; }

    /// <summary>派发队列满时的处理方式。</summary>
    public QueueFullMode QueueFullMode { get; }

    /// <summary>关联模式（已应用 <see cref="ChannelComponents"/> 的覆盖）。</summary>
    public CorrelationMode Correlation { get; }

    /// <summary>关联键提取器（Keyed 模式必需）。</summary>
    public IFrameKeyExtractor? KeyExtractor { get; }

    /// <summary>分帧编解码工厂（已应用覆盖）。</summary>
    public IFrameCodecFactory Codec { get; }

    /// <summary>握手钩子；为 null 表示没有握手。</summary>
    public IConnectionInitializer? Initializer { get; }

    /// <summary>心跳配置的副本。</summary>
    public HeartbeatOptions Heartbeat { get; }

    /// <summary>心跳探测；心跳未启用，或探测由 <see cref="HealthProbeFactory"/> 提供时为 null。</summary>
    public IHealthProbe? HealthProbe { get; }

    /// <summary>代码级探测工厂；心跳未启用时为 null。由驱动在第一次成功打开、启动心跳之前调用一次（以通道自身为参数）。</summary>
    public Func<IByteChannel, IHealthProbe>? HealthProbeFactory { get; }

    /// <summary>重连退避策略；重连未启用时为 null。</summary>
    public IBackoffPolicy? ReconnectPolicy { get; }

    /// <summary>首次连接失败是否转入后台重连（仅在启用重连时有效）。</summary>
    public bool ReconnectOnInitialFailure { get; }

    /// <summary>
    /// 为一次连接创建 <see cref="StreamChannel"/> 的运行参数。
    /// </summary>
    /// <param name="partialFrameAction">半帧超时的处理方式（派生类的 PartialFrameAction）。</param>
    /// <returns>新的运行参数对象。</returns>
    public StreamChannelSettings CreateStreamSettings(PartialFrameAction partialFrameAction)
        => new StreamChannelSettings
        {
            SendTimeout = SendTimeout,
            RequestTimeout = RequestTimeout,
            LateReplyWindow = LateReplyWindow,
            PartialFrameAction = partialFrameAction,
            PartialFrameTimeout = PartialFrameTimeout,
            ResetOnRequestTimeout = ResetOnRequestTimeout,
            Correlation = Correlation,
            ReceiveBufferSize = ReceiveBufferSize,
        };

    private static HeartbeatOptions CopyHeartbeat(HeartbeatOptions source)
    {
        var copy = new HeartbeatOptions
        {
            Enabled = source.Enabled,
            Interval = source.Interval,
            Timeout = source.Timeout,
            MaxFailures = source.MaxFailures,
            OnlyWhenIdle = source.OnlyWhenIdle,
            Payload = source.Payload,
            ExpectedReply = source.ExpectedReply,
        };

        if (copy.Enabled)
        {
            if (copy.Interval <= 0)
                throw new ArgumentOutOfRangeException("Heartbeat.Interval", copy.Interval, "The heartbeat interval must be positive.");
            if (copy.Timeout <= 0)
                throw new ArgumentOutOfRangeException("Heartbeat.Timeout", copy.Timeout, "The heartbeat timeout must be positive.");
            if (copy.MaxFailures < 1)
                throw new ArgumentOutOfRangeException("Heartbeat.MaxFailures", copy.MaxFailures, "The heartbeat failure threshold must be at least 1.");
        }

        return copy;
    }

    // 启用心跳且没有代码级探测时，由心跳负载构造内置探测（D14）。
    private static IHealthProbe CreatePayloadProbe(HeartbeatOptions heartbeat, IByteChannel channel)
    {
        if (string.IsNullOrEmpty(heartbeat.Payload))
            throw new ArgumentException("Heartbeat.Payload is required when neither IHealthProbe nor HealthProbeFactory is supplied.", nameof(ClientChannelSettings.Heartbeat));

        byte[] payload = ParseBytes(heartbeat.Payload!, "Heartbeat.Payload");
        byte[]? expectedReply = string.IsNullOrEmpty(heartbeat.ExpectedReply)
            ? null
            : ParseBytes(heartbeat.ExpectedReply!, "Heartbeat.ExpectedReply");

        return new PayloadHeartbeatProbe(channel, payload, expectedReply, heartbeat.Timeout);
    }

    private static byte[] ParseBytes(string text, string propertyName)
    {
        try
        {
            return ByteSequenceParser.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"{propertyName} is not a valid byte sequence: {ex.Message}", "settings", ex);
        }
    }

    private static int NonNegative(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must not be negative.");

        return value;
    }

    private static int Positive(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must be positive.");

        return value;
    }
}
