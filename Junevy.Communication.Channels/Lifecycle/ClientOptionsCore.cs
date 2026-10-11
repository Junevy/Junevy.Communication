using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 字节流与数据报客户端共用的已校验运行参数（D5，P25 Task 9）。构造时复制并校验，之后不再读取调用方的对象。
/// 校验顺序与两种传输原有的顺序一致：只属于一种传输的设置在它原来的位置检查，因此同时存在多个非法设置时报告的错误不变。
/// </summary>
internal sealed class ClientOptionsCore
{
    private const int MaxDatagramPayload = 65507;

    /// <summary>
    /// 校验并解析配置。
    /// </summary>
    /// <param name="settings">两种传输共用的运行参数；不能为 null。</param>
    /// <param name="components">代码级覆盖；可为 null。</param>
    /// <param name="probeChannel">内置心跳探测执行请求的字节通道；由调用方保证不为 null。</param>
    /// <param name="builtInProbeParamName">内置探测缺少负载时异常的参数名；null 表示异常不带参数名（数据报客户端的既有行为）。</param>
    /// <exception cref="ArgumentException">配置非法（例如启用心跳却没有探测的负载）。</exception>
    /// <exception cref="ArgumentOutOfRangeException">某个数值超出范围。</exception>
    public ClientOptionsCore(ClientCoreSettings settings, ChannelComponents? components, IByteChannel probeChannel, string? builtInProbeParamName)
    {
        if (settings.Heartbeat == null)
            throw new ArgumentException("Heartbeat must not be null.", nameof(settings));
        if (settings.Reconnect == null)
            throw new ArgumentException("Reconnect must not be null.", nameof(settings));
        LifecycleSupport.RequireSingleHealthProbe(components);
        RequireTimingsAndQueue(settings);

        HandshakeTimeout = settings.HandshakeTimeout;
        SendTimeout = settings.SendTimeout;
        RequestTimeout = settings.RequestTimeout;
        IdleTimeout = settings.IdleTimeout;
        DisconnectTimeout = settings.DisconnectTimeout;
        LateReplyWindow = settings.LateReplyWindow;
        QueueCapacity = settings.ReceiveQueueCapacity;
        QueueFullMode = settings.QueueFullMode;

        CorrelationMode correlation = components?.Correlation ?? settings.Correlation;
        if (!Enum.IsDefined(typeof(CorrelationMode), correlation))
            throw new ArgumentOutOfRangeException(nameof(ClientCoreSettings.Correlation), correlation, "Unknown correlation mode.");
        Correlation = correlation;
        KeyExtractor = components?.KeyExtractor;
        if (correlation == CorrelationMode.Keyed && KeyExtractor == null)
            throw new ArgumentException("Keyed correlation requires ChannelComponents.KeyExtractor.", nameof(components));

        // 只有字节流有分帧配置；组件提供的编解码器优先（数据报不使用编解码器）。
        Codec = components?.FrameCodec;
        if (Codec == null && settings.Framing != null)
            Codec = FrameCodecFactory.Create(settings.Framing);
        Initializer = components?.Initializer;

        Heartbeat = HeartbeatSettings.Copy(settings.Heartbeat);
        if (Heartbeat.Enabled)
        {
            // 探测的来源：代码级探测、代码级工厂（由驱动在第一次打开时调用）或内置负载探测。前两者存在时不需要心跳负载。
            HealthProbe = components?.HealthProbe;
            HealthProbeFactory = components?.HealthProbeFactory;
            if (HealthProbe == null && HealthProbeFactory == null)
                HealthProbe = HeartbeatSettings.CreatePayloadProbe(Heartbeat, probeChannel, builtInProbeParamName);
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

    /// <summary>握手时限（毫秒），0 表示不限时。</summary>
    public int HandshakeTimeout { get; }

    /// <summary>单帧或单个数据报写出的超时（毫秒），0 表示不限时。</summary>
    public int SendTimeout { get; }

    /// <summary>请求应答的默认超时（毫秒），0 表示不限时。</summary>
    public int RequestTimeout { get; }

    /// <summary>迟到应答窗口（毫秒），-1 表示等于请求超时。</summary>
    public int LateReplyWindow { get; }

    /// <summary>空闲超时（毫秒），0 表示禁用。</summary>
    public int IdleTimeout { get; }

    /// <summary>优雅关闭的等待时间（毫秒）。</summary>
    public int DisconnectTimeout { get; }

    /// <summary>派发队列容量（帧数）。</summary>
    public int QueueCapacity { get; }

    /// <summary>派发队列满时的处理方式。</summary>
    public QueueFullMode QueueFullMode { get; }

    /// <summary>关联模式（已应用 <see cref="ChannelComponents"/> 的覆盖）。</summary>
    public CorrelationMode Correlation { get; }

    /// <summary>关联键提取器（Keyed 模式必需）。</summary>
    public IFrameKeyExtractor? KeyExtractor { get; }

    /// <summary>分帧编解码工厂（组件优先）；数据报为 null。</summary>
    public IFrameCodecFactory? Codec { get; }

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

    // 时限、重试、缓冲与队列的校验。顺序是两种传输原有顺序的并集：
    // 握手、发送、请求、[重试次数：数据报]、空闲、[未成帧超时：字节流]、断开、迟到窗口、[接收缓冲：字节流 / 最大数据报：数据报]、队列容量、队列满方式。
    private static void RequireTimingsAndQueue(ClientCoreSettings settings)
    {
        NonNegative(settings.HandshakeTimeout, nameof(ClientCoreSettings.HandshakeTimeout));
        NonNegative(settings.SendTimeout, nameof(ClientCoreSettings.SendTimeout));
        NonNegative(settings.RequestTimeout, nameof(ClientCoreSettings.RequestTimeout));
        if (settings.RequestRetryCount is int retryCount)
            NonNegative(retryCount, nameof(ClientCoreSettings.RequestRetryCount));
        NonNegative(settings.IdleTimeout, nameof(ClientCoreSettings.IdleTimeout));
        if (settings.PartialFrameTimeout is int partialFrameTimeout)
            NonNegative(partialFrameTimeout, nameof(ClientCoreSettings.PartialFrameTimeout));
        NonNegative(settings.DisconnectTimeout, nameof(ClientCoreSettings.DisconnectTimeout));
        if (settings.LateReplyWindow < -1)
            throw new ArgumentOutOfRangeException(nameof(ClientCoreSettings.LateReplyWindow), settings.LateReplyWindow,
                "The late reply window must be -1 or a non-negative value.");
        if (settings.ReceiveBufferSize is int receiveBufferSize)
            Positive(receiveBufferSize, nameof(ClientCoreSettings.ReceiveBufferSize));
        if (settings.MaxDatagramSize is int maxDatagramSize && (maxDatagramSize < 1 || maxDatagramSize > MaxDatagramPayload))
            throw new ArgumentOutOfRangeException(nameof(ClientCoreSettings.MaxDatagramSize), maxDatagramSize,
                "The maximum datagram size must be within [1, 65507].");
        Positive(settings.ReceiveQueueCapacity, nameof(ClientCoreSettings.ReceiveQueueCapacity));
        if (!Enum.IsDefined(typeof(QueueFullMode), settings.QueueFullMode))
            throw new ArgumentOutOfRangeException(nameof(ClientCoreSettings.QueueFullMode), settings.QueueFullMode, "Unknown queue full mode.");
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
