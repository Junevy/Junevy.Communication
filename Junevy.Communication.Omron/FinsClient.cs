using Junevy.Communication.Channels;
using Junevy.Communication.Core.Registry;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 客户端（计划第 17 节）：只走 FINSoverTCP（P4-D2），通道按 FINS/TCP 外壳的长度字段分帧、按发送顺序关联应答（P4-D8、P4-D10）。
/// 地址、单包上限与错误映射在钩子部分文件里，帧的编解码在 Protocol/ 与 Framing/ 中。
/// 创建入口是 <see cref="Create(IChannelConfig, FinsClientOptions, ILoggerFactory?)"/>：传输配置或创建器由调用方给出，
/// 分帧与关联由协议设置，不能另行配置。
/// 连接建立时由握手取得节点号，因此 <see cref="ClientNode"/> 与 <see cref="ServerNode"/> 在连接之前为 null（P4-D18）。
/// </summary>
public sealed partial class FinsClient : PlcClientBase<FinsAddress>
{
    /// <summary>内置传输的创建器：本期只有 <see cref="TcpClientChannelConfig"/>（P4-D2）。</summary>
    internal static readonly CreatorCatalog<IChannelCreator> BuiltInCreators = new CreatorCatalog<IChannelCreator>(new IChannelCreator[]
    {
        new TcpClientChannelCreator(),
    });

    private readonly FinsSession session;

    /// <summary>
    /// 创建 FINS 客户端。通道由调用方提供，应已由 <see cref="FinsProtocolProfile"/> 配置；选项在构造时校验并取快照。
    /// 一般情况下请使用 <see cref="Create(IChannelConfig, FinsClientOptions, ILoggerFactory?)"/>，它总是正确配置通道。
    /// </summary>
    /// <param name="channel">客户端拥有的通道（释放客户端时一并释放）。</param>
    /// <param name="settings">连接参数快照。</param>
    /// <param name="options">FINS 选项。</param>
    /// <param name="session">握手结果写入的会话状态，必须与 <paramref name="channel"/> 的初始化器共用。</param>
    /// <param name="logger">日志记录器；为 null 时不输出日志。</param>
    internal FinsClient(IClientChannel channel, FinsProtocolSettings settings, FinsClientOptions options, FinsSession session, ILogger<FinsClient>? logger)
        : base(channel, options, settings.DataFormat, logger ?? NullLogger<FinsClient>.Instance)
        => this.session = session;

    /// <summary>最近一次握手得到的客户端节点号（写入请求的 SA1）；尚未握手时为 null（P4-D18）。</summary>
    public byte? ClientNode => session.Current.Route?.SourceNode;

    /// <summary>最近一次握手得到的服务端节点号（写入请求的 DA1）；尚未握手时为 null（P4-D18）。</summary>
    public byte? ServerNode => session.Current.Route?.DestinationNode;

    /// <summary>创建 FINS 客户端，传输为内置的 TCP 配置。</summary>
    /// <param name="transport">传输配置，精确类型必须是 <see cref="TcpClientChannelConfig"/>；其分帧与关联必须保持默认值。</param>
    /// <param name="options">FINS 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> 或 <paramref name="options"/> 为 null。</exception>
    /// <exception cref="NotSupportedException">传输配置的精确类型不是 <see cref="TcpClientChannelConfig"/>（FINS 只走 TCP，P4-D2）。</exception>
    /// <exception cref="ArgumentOutOfRangeException">客户端节点号是 FINS 的广播地址 255（P4-D18）。</exception>
    public static FinsClient Create(IChannelConfig transport, FinsClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));

        return Create(null, transport, BuiltInCreators.Resolve(transport), options, loggerFactory);
    }

    /// <summary>创建 FINS 客户端，传输由调用方提供的通道创建器创建（自定义传输）。</summary>
    /// <param name="transport">传输配置，精确类型必须是 <paramref name="transportCreator"/> 的 <c>ConfigType</c>。</param>
    /// <param name="transportCreator">通道创建器；只有字节流交付的创建器可用（P4-D10）。</param>
    /// <param name="options">FINS 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/>、<paramref name="transportCreator"/> 或 <paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">
    /// 传输配置的精确类型与创建器不匹配；创建器交付数据报而不是字节流；或客户端节点号是 FINS 的广播地址 255。
    /// </exception>
    public static FinsClient Create(IChannelConfig transport, IChannelCreator transportCreator, FinsClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transportCreator == null)
            throw new ArgumentNullException(nameof(transportCreator));

        return Create(null, transport, transportCreator, options, loggerFactory);
    }

    /// <summary>带名称的创建（供依赖注入的创建器使用）：选项先取快照再创建通道，构造客户端失败时释放已创建的通道。</summary>
    internal static FinsClient Create(string? name, IChannelConfig transport, IChannelCreator creator, FinsClientOptions options, ILoggerFactory? loggerFactory)
    {
        FinsProtocolSettings snapshot = FinsProtocolSettings.From(options);
        var session = new FinsSession();
        IClientChannel channel = PlcChannelBinding.CreateChannel(
            creator,
            name,
            transport,
            (delivery, medium) => FinsProtocolProfile.Configure(new ChannelComponents(), snapshot, session, delivery),
            loggerFactory);
        try
        {
            return new FinsClient(channel, snapshot, options, session, loggerFactory?.CreateLogger<FinsClient>());
        }
        catch
        {
            // 客户端构造失败（例如 PlcClientOptions 非法）时，通道没有其他所有者，在此释放；异常原样抛出。
            channel.Dispose();
            throw;
        }
    }
}
