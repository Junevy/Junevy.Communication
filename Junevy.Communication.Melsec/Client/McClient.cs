using Junevy.Communication.Channels;
using Junevy.Communication.Core.Registry;
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
/// 创建入口是 <c>McClient.Create</c>：内置传输（TCP、UDP、串口）直接传入配置，自定义传输传入其通道创建器；
/// 传输配置中的分帧与关联由协议设置，不能另行设置。帧类型与介质的规则由 <see cref="McProtocolProfile"/> 统一校验。
/// </summary>
public sealed partial class McClient : PlcClientBase<McAddress>
{
    /// <summary>内置传输的创建器：<see cref="TcpClientChannelConfig"/>、<see cref="UdpChannelConfig"/> 与 <see cref="SerialChannelConfig"/>。</summary>
    internal static readonly CreatorCatalog<IChannelCreator> BuiltInCreators = new CreatorCatalog<IChannelCreator>(new IChannelCreator[]
    {
        new TcpClientChannelCreator(),
        new UdpChannelCreator(),
        new SerialChannelCreator(),
    });

    private readonly McProtocolSettings settings;
    private readonly McFrameExchange exchange;

    /// <summary>
    /// 创建 MELSEC 客户端。通道由调用方提供，应已由 <see cref="McProtocolProfile"/> 的 <c>Configure</c> 配置；选项在构造时校验并取快照。
    /// 一般情况下请使用 <c>McClient.Create</c>，它总是正确配置通道。
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

    /// <summary>创建 MELSEC 客户端，传输为内置的 TCP、UDP 或串口配置。</summary>
    /// <param name="transport">
    /// 传输配置：<see cref="TcpClientChannelConfig"/>、<see cref="UdpChannelConfig"/> 或 <see cref="SerialChannelConfig"/>；其分帧与关联必须保持默认值。
    /// </param>
    /// <param name="options">MELSEC 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> 为 null。</exception>
    /// <exception cref="NotSupportedException">传输配置的精确类型不是内置的三种配置之一。</exception>
    /// <exception cref="ArgumentException">帧类型与传输的介质不匹配（以太网只接受 3E、4E，串口只接受 4C）；或传输配置设置了分帧或关联。</exception>
    public static McClient Create(IChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));

        return Create(null, transport, BuiltInCreators.Resolve(transport), options, loggerFactory);
    }

    /// <summary>创建 MELSEC 客户端，传输由调用方提供的通道创建器创建（自定义传输）。</summary>
    /// <param name="transport">传输配置，精确类型必须是 <paramref name="transportCreator"/> 的 <c>ConfigType</c>。</param>
    /// <param name="transportCreator">通道创建器；其交付方式与介质决定协议如何配置通道与校验帧类型。</param>
    /// <param name="options">MELSEC 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> 或 <paramref name="transportCreator"/> 为 null。</exception>
    /// <exception cref="ArgumentException">
    /// 传输配置的精确类型与创建器不匹配；帧类型与创建器的交付方式或介质不匹配；或传输配置设置了协议所用的分帧或关联。
    /// </exception>
    public static McClient Create(IChannelConfig transport, IChannelCreator transportCreator, McClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transportCreator == null)
            throw new ArgumentNullException(nameof(transportCreator));

        return Create(null, transport, transportCreator, options, loggerFactory);
    }

    /// <summary>带名称的创建（供依赖注入的创建器使用）：选项取快照，经 <see cref="PlcChannelBinding"/> 由创建器创建通道，再构造客户端。</summary>
    internal static McClient Create(string? name, IChannelConfig transport, IChannelCreator creator, McClientOptions options, ILoggerFactory? loggerFactory)
    {
        McProtocolSettings snapshot = McProtocolSettings.From(options);
        IClientChannel channel = PlcChannelBinding.CreateChannel(
            creator,
            name,
            transport,
            (delivery, medium) => McProtocolProfile.Configure(new ChannelComponents(), snapshot, delivery, medium),
            loggerFactory);
        try
        {
            return new McClient(channel, snapshot, options, loggerFactory?.CreateLogger<McClient>());
        }
        catch
        {
            // 客户端构造失败（例如 PlcClientOptions 非法）时，通道没有其他所有者，在此释放；异常原样抛出。
            channel.Dispose();
            throw;
        }
    }
}
