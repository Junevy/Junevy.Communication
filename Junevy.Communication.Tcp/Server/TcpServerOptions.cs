using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
        // 服务端不接受 HealthProbe（同时设置 HealthProbeFactory 时也由此拒绝，因此无需单独的互斥校验）。
        if (components?.HealthProbe != null)
            throw new ArgumentException("ChannelComponents.HealthProbe is one instance shared by every session and cannot be bound to a single session; use ChannelComponents.HealthProbeFactory instead.", nameof(components));

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
        ResetOnRequestTimeout = config.ResetOnRequestTimeout;
        LateReplyWindow = RequireLateReplyWindow(config.LateReplyWindow);
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
        HealthProbeFactory = components?.HealthProbeFactory;

        Heartbeat = CopyHeartbeat(config.Heartbeat);
        if (Heartbeat.Enabled && HealthProbeFactory == null)
        {
            HeartbeatPayload = ParseBytes(RequirePayload(Heartbeat.Payload), "Heartbeat.Payload");
            HeartbeatExpectedReply = string.IsNullOrEmpty(Heartbeat.ExpectedReply)
                ? null
                : ParseBytes(Heartbeat.ExpectedReply!, "Heartbeat.ExpectedReply");
        }

        Socket = TcpSocketConfigurator.ValidateAndCopy(config.Socket, nameof(config));
        RestartPolicy = BackoffPolicyFactory.Create(config.RestartOnFault);

        TlsEnabled = config.Tls.Enabled;
        if (TlsEnabled)
        {
            // 服务端证书：组件提供的优先（由调用方持有）；否则使用配置来源（构造时只校验结构，加载由 TcpServer 在构造时完成）。
            ComponentServerCertificate = components?.ServerCertificate;
            if (ComponentServerCertificate != null)
            {
                if (!ComponentServerCertificate.HasPrivateKey)
                    throw new ArgumentException("TcpChannelComponents.ServerCertificate must include its private key.", nameof(components));
            }
            else if (config.Tls.ServerCertificate != null)
            {
                CertificateLoader.Validate(config.Tls.ServerCertificate, "Tls.ServerCertificate");
                ServerCertificateSource = CertificateLoader.Copy(config.Tls.ServerCertificate);
            }
            else
            {
                throw new ArgumentException("TLS is enabled but no server certificate is configured (Tls.ServerCertificate or TcpChannelComponents.ServerCertificate).",
                                            nameof(config));
            }

            TlsProtocols = config.Tls.Protocols;
            TlsCheckRevocation = config.Tls.CheckCertificateRevocation;
            ClientCertificateRequired = config.Tls.ClientCertificateRequired;
        }

        RemoteCertificateValidation = components?.RemoteCertificateValidation;
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

    /// <summary>会话请求的应答超时（毫秒）。</summary>
    public int RequestTimeout { get; }

    /// <summary>请求超时后是否关闭会话。</summary>
    public bool ResetOnRequestTimeout { get; }

    /// <summary>迟到应答窗口（毫秒），-1 表示等于请求超时。</summary>
    public int LateReplyWindow { get; }

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

    /// <summary>心跳探测工厂（<see cref="ChannelComponents.HealthProbeFactory"/>）：每个会话启动心跳时以该会话为参数调用一次；为 null 时使用绑定该会话通道的内置负载探测。</summary>
    public Func<IByteChannel, IHealthProbe>? HealthProbeFactory { get; }

    /// <summary>心跳配置的副本。</summary>
    public HeartbeatOptions Heartbeat { get; }

    /// <summary>内置探测的心跳负载（已解析）；未启用或使用工厂探测时为 null。</summary>
    public byte[]? HeartbeatPayload { get; }

    /// <summary>内置探测的期望应答（已解析，精确匹配）；为 null 时只要求发送成功。</summary>
    public byte[]? HeartbeatExpectedReply { get; }

    /// <summary>套接字选项的副本。</summary>
    public TcpSocketOptions Socket { get; }

    /// <summary>重新监听的退避策略；未启用时为 null。</summary>
    public IBackoffPolicy? RestartPolicy { get; }

    /// <summary>是否启用 TLS。</summary>
    public bool TlsEnabled { get; }

    /// <summary>组件提供的服务端证书（由调用方持有）；为 null 时使用 <see cref="ServerCertificateSource"/>。仅在启用 TLS 时有效。</summary>
    public X509Certificate2? ComponentServerCertificate { get; }

    /// <summary>配置中的服务端证书来源（构造时复制）；启用 TLS 且没有组件证书时非 null。</summary>
    public CertificateSource? ServerCertificateSource { get; }

    /// <summary>TLS 协议版本；<see cref="SslProtocols.None"/> 表示交给操作系统。</summary>
    public SslProtocols TlsProtocols { get; }

    /// <summary>是否检查证书吊销。</summary>
    public bool TlsCheckRevocation { get; }

    /// <summary>是否要求客户端证书（双向认证）。</summary>
    public bool ClientCertificateRequired { get; }

    /// <summary>远端证书校验回调（组件提供，用于校验客户端证书）；为 null 时使用默认规则。</summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; }

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
            throw new ArgumentException("Heartbeat.Payload is required when no HealthProbeFactory is supplied.", nameof(TcpServerConfig.Heartbeat));

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

    // 迟到应答窗口：-1 表示等于请求超时，0 表示不记录迟到应答，其余为固定毫秒数；小于 -1 非法。
    private static int RequireLateReplyWindow(int value)
    {
        if (value < -1)
            throw new ArgumentOutOfRangeException(nameof(TcpServerConfig.LateReplyWindow), value, "The late reply window must be -1 or a non-negative value.");

        return value;
    }
}
