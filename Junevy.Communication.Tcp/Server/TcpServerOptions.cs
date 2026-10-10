using System.Net;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// TCP 服务端的已校验运行参数（内部，D5）。构造时从 <see cref="TcpServerConfig"/> 与 <see cref="TcpChannelComponents"/> 解析并复制，
/// 之后不再读取调用方的对象。非法值抛出 <see cref="ArgumentException"/> 族。
/// </summary>
internal sealed class TcpServerOptions
{
    private const int MaxPort = 65535;

    /// <summary>
    /// 校验并解析配置。
    /// </summary>
    /// <param name="config">服务端配置；不能为 null。</param>
    /// <param name="components">代码级覆盖；可为 null。</param>
    /// <exception cref="ArgumentNullException">配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public TcpServerOptions(TcpServerConfig config, TcpChannelComponents? components)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (config.Framing == null)
            throw new ArgumentException("Framing must not be null.", nameof(config));
        if (config.Heartbeat == null)
            throw new ArgumentException("Heartbeat must not be null.", nameof(config));
        if (config.RestartOnFault == null)
            throw new ArgumentException("RestartOnFault must not be null.", nameof(config));
        if (config.Tls == null)
            throw new ArgumentException("Tls must not be null.", nameof(config));

        ListenAddress = ParseListenAddress(config.ListenAddress);
        Port = RequireRange(config.Port, 1, MaxPort, nameof(TcpServerConfig.Port));
        Backlog = RequirePositive(config.Backlog, nameof(TcpServerConfig.Backlog));
        MaxSessions = RequireNonNegative(config.MaxSessions, nameof(TcpServerConfig.MaxSessions));
        AllowedRemoteAddresses = ParseWhitelist(config.AllowedRemoteAddresses);

        SessionHandshakeTimeout = RequirePositive(config.SessionHandshakeTimeout, nameof(TcpServerConfig.SessionHandshakeTimeout));
        SessionIdleTimeout = RequireNonNegative(config.SessionIdleTimeout, nameof(TcpServerConfig.SessionIdleTimeout));
        PartialFrameTimeout = RequireNonNegative(config.PartialFrameTimeout, nameof(TcpServerConfig.PartialFrameTimeout));
        SendTimeout = RequirePositive(config.SendTimeout, nameof(TcpServerConfig.SendTimeout));
        RequestTimeout = RequirePositive(config.RequestTimeout, nameof(TcpServerConfig.RequestTimeout));
        StopTimeout = RequirePositive(config.StopTimeout, nameof(TcpServerConfig.StopTimeout));
        ReceiveQueueCapacity = RequirePositive(config.ReceiveQueueCapacity, nameof(TcpServerConfig.ReceiveQueueCapacity));

        Correlation = components?.Correlation ?? config.Correlation;
        if (!Enum.IsDefined(typeof(CorrelationMode), Correlation))
            throw new ArgumentOutOfRangeException(nameof(TcpServerConfig.Correlation), Correlation, "Unknown correlation mode.");

        KeyExtractor = components?.KeyExtractor;
        if (Correlation == CorrelationMode.Keyed && KeyExtractor == null)
            throw new ArgumentException("Keyed correlation requires ChannelComponents.KeyExtractor.", nameof(components));

        Codec = components?.FrameCodec ?? FrameCodecFactory.Create(config.Framing);
        Initializer = components?.Initializer;
        ConnectionFilter = components?.ConnectionFilter;
        HealthProbe = components?.HealthProbe;

        Heartbeat = CopyHeartbeat(config.Heartbeat);
        if (Heartbeat.Enabled && HealthProbe == null)
        {
            HeartbeatPayload = ParseBytes(RequirePayload(Heartbeat.Payload), "Heartbeat.Payload");
            HeartbeatExpectedReply = string.IsNullOrEmpty(Heartbeat.ExpectedReply)
                ? null
                : ParseBytes(Heartbeat.ExpectedReply!, "Heartbeat.ExpectedReply");
        }

        Socket = TcpSocketConfigurator.ValidateAndCopy(config.Socket, nameof(config));
        RestartPolicy = BackoffPolicyFactory.Create(config.RestartOnFault);
        TlsEnabled = config.Tls.Enabled;
    }

    /// <summary>监听地址。</summary>
    public IPAddress ListenAddress { get; }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>监听队列长度。</summary>
    public int Backlog { get; }

    /// <summary>最大会话数；0 表示不限。</summary>
    public int MaxSessions { get; }

    /// <summary>允许的远端地址；null 表示不限制来源。</summary>
    public IPAddress[]? AllowedRemoteAddresses { get; }

    /// <summary>会话握手超时（毫秒）。</summary>
    public int SessionHandshakeTimeout { get; }

    /// <summary>会话空闲超时（毫秒），0 表示禁用。</summary>
    public int SessionIdleTimeout { get; }

    /// <summary>未成帧数据的最长保留时间（毫秒），0 表示禁用。</summary>
    public int PartialFrameTimeout { get; }

    /// <summary>单帧写出超时（毫秒）。</summary>
    public int SendTimeout { get; }

    /// <summary>请求应答超时（毫秒）。</summary>
    public int RequestTimeout { get; }

    /// <summary>停止等待时限（毫秒）。</summary>
    public int StopTimeout { get; }

    /// <summary>派发队列容量（帧数）。</summary>
    public int ReceiveQueueCapacity { get; }

    /// <summary>关联模式（已应用 <see cref="ChannelComponents"/> 的覆盖）。</summary>
    public CorrelationMode Correlation { get; }

    /// <summary>关联键提取器（Keyed 模式必需）。</summary>
    public IFrameKeyExtractor? KeyExtractor { get; }

    /// <summary>分帧编解码工厂（已应用覆盖）。每个会话创建自己的分帧器，编码器由工厂共享（无状态）。</summary>
    public IFrameCodecFactory Codec { get; }

    /// <summary>每个会话的握手钩子；为 null 表示没有握手。</summary>
    public IConnectionInitializer? Initializer { get; }

    /// <summary>连接过滤器；为 null 表示不过滤。</summary>
    public IConnectionFilter? ConnectionFilter { get; }

    /// <summary>代码级心跳探测（由 <see cref="ChannelComponents.HealthProbe"/> 提供）；为 null 时每个会话使用内置的负载探测。</summary>
    public IHealthProbe? HealthProbe { get; }

    /// <summary>心跳配置的副本。</summary>
    public HeartbeatOptions Heartbeat { get; }

    /// <summary>内置探测的心跳负载（已解析）；未启用或使用代码级探测时为 null。</summary>
    public byte[]? HeartbeatPayload { get; }

    /// <summary>内置探测的期望应答（已解析，精确匹配）；为 null 时只要求发送成功。</summary>
    public byte[]? HeartbeatExpectedReply { get; }

    /// <summary>套接字选项的副本。</summary>
    public TcpSocketOptions Socket { get; }

    /// <summary>重新监听的退避策略；未启用时为 null。</summary>
    public IBackoffPolicy? RestartPolicy { get; }

    /// <summary>是否启用 TLS（本阶段启动时返回 NotSupported）。</summary>
    public bool TlsEnabled { get; }

    private static IPAddress ParseListenAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text, out IPAddress? address) || address == null)
            throw new ArgumentException("ListenAddress must be an IP address.", nameof(TcpServerConfig.ListenAddress));

        return address;
    }

    private static IPAddress[]? ParseWhitelist(string[]? text)
    {
        if (text == null)
            return null;

        var addresses = new IPAddress[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(text[i]) || !IPAddress.TryParse(text[i], out IPAddress? address) || address == null)
                throw new ArgumentException("AllowedRemoteAddresses must contain only IP addresses.", nameof(TcpServerConfig.AllowedRemoteAddresses));

            addresses[i] = address;
        }

        return addresses;
    }

    // 心跳配置的复制与校验（与客户端的 StreamClientOptions 规则一致）。探测负载在使用内置探测时另行检查。
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

    private static string RequirePayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            throw new ArgumentException("Heartbeat.Payload is required when no IHealthProbe is supplied.", nameof(TcpServerConfig.Heartbeat));

        return payload!;
    }

    private static byte[] ParseBytes(string text, string propertyName)
    {
        try
        {
            return ByteSequenceParser.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"{propertyName} is not a valid byte sequence: {ex.Message}", nameof(TcpServerConfig.Heartbeat), ex);
        }
    }

    private static int RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"The value must be within [{min}, {max}].");

        return value;
    }

    private static int RequirePositive(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must be positive.");

        return value;
    }

    private static int RequireNonNegative(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must not be negative.");

        return value;
    }
}
