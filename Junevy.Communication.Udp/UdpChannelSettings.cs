using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Lifecycle;

namespace Junevy.Communication.Udp;

/// <summary>
/// UDP 通道的已校验运行参数（D5）。构造时从 <see cref="UdpChannelConfig"/> 与 <see cref="ChannelComponents"/> 解析并复制，之后不再读取调用方的对象。
/// 套接字参数保存在本类（打开每个连接时使用）；数据报的通用参数交给 <see cref="DatagramClientSettings"/>，由 <see cref="DatagramClientOptions"/> 进一步校验。
/// </summary>
internal sealed class UdpChannelSettings
{
    private const int MaxPort = 65535;
    private const int MaxDatagramPayload = 65507;

    private UdpChannelSettings()
    {
    }

    /// <summary>本地绑定地址。</summary>
    public IPAddress LocalAddress { get; private set; } = IPAddress.Any;

    /// <summary>本地绑定端口（0 表示由系统分配）。</summary>
    public int LocalPort { get; private set; }

    /// <summary>定向模式的远端主机（名称或 IP 字面量）；非定向模式为 null。</summary>
    public string? RemoteHost { get; private set; }

    /// <summary>定向模式的远端端口。</summary>
    public int RemotePort { get; private set; }

    /// <summary>是否允许广播。</summary>
    public bool EnableBroadcast { get; private set; }

    /// <summary>需要加入的组播组。</summary>
    public IReadOnlyList<IPAddress> MulticastGroups { get; private set; } = Array.Empty<IPAddress>();

    /// <summary>组播 TTL。</summary>
    public short MulticastTimeToLive { get; private set; }

    /// <summary>组播回环。</summary>
    public bool MulticastLoopback { get; private set; }

    /// <summary>是否设置 SO_REUSEADDR。</summary>
    public bool ReuseAddress { get; private set; }

    /// <summary>套接字接收缓冲（SO_RCVBUF）。</summary>
    public int ReceiveBufferSize { get; private set; }

    /// <summary>数据报通道的通用参数。</summary>
    public DatagramClientSettings Client { get; private set; } = new DatagramClientSettings();

    /// <summary>
    /// 校验配置并生成运行参数（D5）。非法时抛出 <see cref="ArgumentException"/> 族；调用方的配置对象不会被修改。
    /// </summary>
    /// <param name="config">UDP 配置；不能为 null。</param>
    /// <param name="components">代码级覆盖；可为 null。</param>
    /// <returns>校验后的运行参数。</returns>
    public static UdpChannelSettings From(UdpChannelConfig config, ChannelComponents? components)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (config.Heartbeat == null)
            throw new ArgumentException("Heartbeat must not be null.", nameof(config));
        if (config.Reconnect == null)
            throw new ArgumentException("Reconnect must not be null.", nameof(config));
        if (components?.FrameCodec != null)
            throw new ArgumentException("UDP channels do not frame datagrams; ChannelComponents.FrameCodec must be null.", nameof(components));

        IPAddress localAddress = ParseAddress(config.LocalAddress, nameof(UdpChannelConfig.LocalAddress));
        InRange(config.LocalPort, 0, MaxPort, nameof(UdpChannelConfig.LocalPort));

        // RemoteHost 与 RemotePort 必须同时设置（定向模式）或同时不设置（非定向模式）。
        bool hasRemoteHost = !string.IsNullOrWhiteSpace(config.RemoteHost);
        bool hasRemotePort = config.RemotePort != 0;
        if (hasRemoteHost != hasRemotePort)
            throw new ArgumentException("RemoteHost and RemotePort must be set together (directed mode) or both left unset.");
        if (hasRemoteHost)
        {
            if (!IsValidHost(config.RemoteHost!))
                throw new ArgumentException($"RemoteHost '{config.RemoteHost}' is neither an IP address nor a valid host name.");
            InRange(config.RemotePort, 1, MaxPort, nameof(UdpChannelConfig.RemotePort));
        }

        IPAddress[] groups = ParseGroups(config.MulticastGroups);
        InRange(config.MulticastTimeToLive, 0, 255, nameof(UdpChannelConfig.MulticastTimeToLive));
        Positive(config.ReceiveBufferSize, nameof(UdpChannelConfig.ReceiveBufferSize));
        InRange(config.MaxDatagramSize, 1, MaxDatagramPayload, nameof(UdpChannelConfig.MaxDatagramSize));
        Positive(config.HandshakeTimeout, nameof(UdpChannelConfig.HandshakeTimeout));
        Positive(config.SendTimeout, nameof(UdpChannelConfig.SendTimeout));
        Positive(config.RequestTimeout, nameof(UdpChannelConfig.RequestTimeout));
        NonNegative(config.DisconnectTimeout, nameof(UdpChannelConfig.DisconnectTimeout));
        NonNegative(config.RequestRetryCount, nameof(UdpChannelConfig.RequestRetryCount));
        NonNegative(config.IdleTimeout, nameof(UdpChannelConfig.IdleTimeout));
        if (config.LateReplyWindow < -1)
            throw new ArgumentOutOfRangeException(nameof(UdpChannelConfig.LateReplyWindow), config.LateReplyWindow,
                "The late reply window must be -1 or a non-negative value.");

        // 内置心跳探测发往远端，因此非定向模式必须由协议包提供探测（ChannelComponents.HealthProbe）。
        if (config.Heartbeat.Enabled && components?.HealthProbe == null && !hasRemoteHost)
            throw new ArgumentException("The built-in heartbeat probe is sent to the remote endpoint, so it requires a directed channel (RemoteHost); "
                                        + "supply ChannelComponents.HealthProbe for an undirected channel.");

        // UDP 发送几乎总是成功：内置探测只判断发送，无法发现对端沉默，因此必须指定期望的应答（设计 5.4、D14）。
        if (config.Heartbeat.Enabled && components?.HealthProbe == null && string.IsNullOrEmpty(config.Heartbeat.ExpectedReply))
            throw new ArgumentException("The built-in heartbeat of a UDP channel must set Heartbeat.ExpectedReply: a UDP send almost always succeeds, "
                                        + "so the probe could not detect a silent peer. Set ExpectedReply or supply ChannelComponents.HealthProbe.");

        return new UdpChannelSettings
        {
            LocalAddress = localAddress,
            LocalPort = config.LocalPort,
            RemoteHost = hasRemoteHost ? config.RemoteHost : null,
            RemotePort = hasRemoteHost ? config.RemotePort : 0,
            EnableBroadcast = config.EnableBroadcast,
            MulticastGroups = groups,
            MulticastTimeToLive = (short)config.MulticastTimeToLive,
            MulticastLoopback = config.MulticastLoopback,
            ReuseAddress = config.ReuseAddress,
            ReceiveBufferSize = config.ReceiveBufferSize,
            Client = new DatagramClientSettings
            {
                HandshakeTimeout = config.HandshakeTimeout,
                SendTimeout = config.SendTimeout,
                RequestTimeout = config.RequestTimeout,
                RequestRetryCount = config.RequestRetryCount,
                LateReplyWindow = config.LateReplyWindow,
                IdleTimeout = config.IdleTimeout,
                DisconnectTimeout = config.DisconnectTimeout,
                MaxDatagramSize = config.MaxDatagramSize,
                ReceiveQueueCapacity = config.ReceiveQueueCapacity,
                QueueFullMode = config.QueueFullMode,
                Correlation = config.Correlation,
                Heartbeat = config.Heartbeat,
                Reconnect = config.Reconnect,
            },
        };
    }

    // 主机名可以是 IP 字面量或语法合法的 DNS 名称；不在此解析 DNS（构造期间不做网络 I/O，解析在打开时进行）。
    private static bool IsValidHost(string host)
        => IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns;

    private static IPAddress[] ParseGroups(string[]? texts)
    {
        if (texts == null)
            return Array.Empty<IPAddress>();

        var groups = new IPAddress[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            IPAddress address = ParseAddress(texts[i], nameof(UdpChannelConfig.MulticastGroups));
            if (!IsMulticast(address))
                throw new ArgumentException($"MulticastGroups entry '{texts[i]}' is not a multicast address.");

            groups[i] = address;
        }

        return groups;
    }

    private static IPAddress ParseAddress(string? text, string property)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text, out IPAddress? address))
            throw new ArgumentException($"{property} must be an IP address literal, not '{text}'.");

        return address!;
    }

    // IPv4 组播为 224.0.0.0/4，IPv6 组播为 ff00::/8。
    private static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6Multicast;

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] >= 224 && bytes[0] <= 239;
    }

    private static void InRange(int value, int min, int max, string property)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(property, value, $"{property} must be within [{min}, {max}].");
    }

    private static void Positive(int value, string property)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(property, value, $"{property} must be positive.");
    }

    private static void NonNegative(int value, string property)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(property, value, $"{property} must not be negative.");
    }
}
