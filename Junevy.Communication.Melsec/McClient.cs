using Junevy.Communication.Channels;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;
using Junevy.Communication.Melsec.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC MC 协议客户端（P2 计划 Task 11）：选项 → 帧选择 → 七个钩子的实现。帧与分帧细节在 Protocol/ 与 Framing/ 中。
/// 工厂方法按传输创建通道：TCP 与 UDP 只接受 3E、4E，串口只接受 4C；传输配置中的分帧与关联由 McClientOptions 决定，不能另行设置。
/// </summary>
public sealed partial class McClient : PlcClientBase<McAddress>
{
    private readonly McProtocolSettings settings;
    private readonly McFrameExchange exchange;

    /// <summary>
    /// 创建 MELSEC 客户端。通道由调用方提供，应已由 <see cref="McProtocolProfile"/> 配置；选项在构造时校验并取快照。
    /// </summary>
    /// <param name="channel">客户端拥有的通道（释放客户端时一并释放）。</param>
    /// <param name="options">MELSEC 选项。</param>
    /// <param name="logger">日志记录器；为 null 时不输出日志。</param>
    public McClient(IClientChannel channel, McClientOptions options, ILogger<McClient>? logger = null)
        : this(channel, McProtocolSettings.From(options), options, logger)
    {
    }

    private McClient(IClientChannel channel, McProtocolSettings settings, McClientOptions options, ILogger<McClient>? logger)
        : base(channel, options, settings.DataFormat, logger ?? NullLogger<McClient>.Instance)
    {
        this.settings = settings;
        exchange = new McFrameExchange(settings);
    }

    /// <summary>创建 TCP 客户端（3E 或 4E）。</summary>
    /// <param name="transport">TCP 传输配置（TLS 等仍由它配置）；其分帧必须保持默认值，关联必须保持默认值或与协议所用的值相同。</param>
    /// <param name="options">MELSEC 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentException">帧类型不是 3E 或 4E，或传输配置设置了分帧或关联。</exception>
    public static McClient CreateTcp(TcpClientChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory = null)
        => CreateTcp(null, transport, options, loggerFactory);

    /// <summary>创建 UDP 客户端（3E 或 4E；数据报不分帧）。</summary>
    /// <param name="transport">UDP 传输配置；其关联必须保持默认值或与协议所用的值相同。</param>
    /// <param name="options">MELSEC 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentException">帧类型不是 3E 或 4E，或传输配置设置了关联。</exception>
    public static McClient CreateUdp(UdpChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory = null)
        => CreateUdp(null, transport, options, loggerFactory);

    /// <summary>创建串口客户端（4C，格式 4 或格式 5）。</summary>
    /// <param name="transport">串口传输配置；其分帧必须保持默认值，关联必须保持默认值或与协议所用的值相同。</param>
    /// <param name="options">MELSEC 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentException">帧类型不是 4C，或传输配置设置了分帧或关联。</exception>
    public static McClient CreateSerial(SerialChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory = null)
        => CreateSerial(null, transport, options, loggerFactory);

    /// <summary>带名称的 TCP 创建（供依赖注入的创建器使用）。</summary>
    internal static McClient CreateTcp(string? name, TcpClientChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory)
    {
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));

        McProtocolSettings snapshot = McProtocolSettings.From(options);
        RequireEthernet(snapshot);

        TcpChannelComponents components = McProtocolProfile.ConfigureStream(new TcpChannelComponents(), snapshot);
        ILogger<TcpClientChannel>? channelLogger = loggerFactory?.CreateLogger<TcpClientChannel>();
        IClientChannel channel = name == null
            ? new TcpClientChannel(transport, channelLogger, components)
            : new TcpClientChannel(name, transport, channelLogger, components);
        return new McClient(channel, snapshot, options, loggerFactory?.CreateLogger<McClient>());
    }

    /// <summary>带名称的 UDP 创建（供依赖注入的创建器使用）。</summary>
    internal static McClient CreateUdp(string? name, UdpChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory)
    {
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));

        McProtocolSettings snapshot = McProtocolSettings.From(options);
        RequireEthernet(snapshot);

        ChannelComponents components = McProtocolProfile.ConfigureDatagram(new ChannelComponents(), snapshot, nameof(options));
        ILogger<UdpChannel>? channelLogger = loggerFactory?.CreateLogger<UdpChannel>();
        IClientChannel channel = name == null
            ? new UdpChannel(transport, channelLogger, components)
            : new UdpChannel(name, transport, channelLogger, components);
        return new McClient(channel, snapshot, options, loggerFactory?.CreateLogger<McClient>());
    }

    /// <summary>带名称的串口创建（供依赖注入的创建器使用）。</summary>
    internal static McClient CreateSerial(string? name, SerialChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory)
    {
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));

        McProtocolSettings snapshot = McProtocolSettings.From(options);
        RequireSerial(snapshot);

        ChannelComponents components = McProtocolProfile.ConfigureStream(new ChannelComponents(), snapshot);
        ILogger<SerialChannel>? channelLogger = loggerFactory?.CreateLogger<SerialChannel>();
        IClientChannel channel = name == null
            ? new SerialChannel(transport, channelLogger, components)
            : new SerialChannel(name, transport, channelLogger, components);
        return new McClient(channel, snapshot, options, loggerFactory?.CreateLogger<McClient>());
    }

    private static void RequireEthernet(McProtocolSettings snapshot)
    {
        if (snapshot.Is4C)
            throw new ArgumentException("TCP and UDP carry 3E and 4E frames only; use CreateSerial for 4C.", "options");
    }

    private static void RequireSerial(McProtocolSettings snapshot)
    {
        if (!snapshot.Is4C)
            throw new ArgumentException("CreateSerial carries 4C frames only; set FrameType to Frame4C.", "options");
    }
}
