using Junevy.Communication.Channels;
using Junevy.Communication.Core.Registry;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7comm 客户端（计划第 13 节）：只走 S7overTCP（P4-D2），通道按 TPKT 长度分帧、按发送顺序关联应答（P4-D8、P4-D10）。
/// 地址、单包上限与错误映射在钩子部分文件里，帧的编解码在 Protocol/ 与 Framing/ 中。
/// 创建入口是 <see cref="Create(IChannelConfig, S7ClientOptions, ILoggerFactory?)"/>：传输配置或创建器由调用方给出，
/// 分帧与关联由协议设置，不能另行配置。
/// </summary>
public sealed partial class S7Client : PlcClientBase<S7Address>
{
    /// <summary>内置传输的创建器：本期只有 <see cref="TcpClientChannelConfig"/>（P4-D2）。</summary>
    internal static readonly CreatorCatalog<IChannelCreator> BuiltInCreators = new CreatorCatalog<IChannelCreator>(new IChannelCreator[]
    {
        new TcpClientChannelCreator(),
    });

    private readonly S7Session session;

    /// <summary>
    /// 创建 S7 客户端。通道由调用方提供，应已由 <see cref="S7ProtocolProfile"/> 配置；选项在构造时校验并取快照。
    /// 一般情况下请使用 <see cref="Create(IChannelConfig, S7ClientOptions, ILoggerFactory?)"/>，它总是正确配置通道。
    /// </summary>
    /// <param name="channel">客户端拥有的通道（释放客户端时一并释放）。</param>
    /// <param name="settings">连接参数快照。</param>
    /// <param name="options">S7 选项。</param>
    /// <param name="session">握手结果写入的会话状态，必须与 <paramref name="channel"/> 的初始化器共用。</param>
    /// <param name="logger">日志记录器；为 null 时不输出日志。</param>
    internal S7Client(IClientChannel channel, S7ProtocolSettings settings, S7ClientOptions options, S7Session session, ILogger<S7Client>? logger)
        : base(channel, options, settings.DataFormat, logger ?? NullLogger<S7Client>.Instance)
        => this.session = session;

    /// <summary>最近一次握手协商的 PDU 字节数；尚未握手时为 null（P4-D9）。</summary>
    public int? NegotiatedPduSize => session.Current.NegotiatedPduBytes;

    /// <summary>创建 S7 客户端，传输为内置的 TCP 配置。</summary>
    /// <param name="transport">传输配置，精确类型必须是 <see cref="TcpClientChannelConfig"/>；其分帧与关联必须保持默认值。</param>
    /// <param name="options">S7 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> 或 <paramref name="options"/> 为 null。</exception>
    /// <exception cref="NotSupportedException">传输配置的精确类型不是 <see cref="TcpClientChannelConfig"/>（S7 只走 TCP，P4-D2）。</exception>
    /// <exception cref="ArgumentException">传输配置设置了分帧或关联；或 Rack、槽号、连接类型超出 P4-D19 的范围。</exception>
    public static S7Client Create(IChannelConfig transport, S7ClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));

        return Create(null, transport, BuiltInCreators.Resolve(transport), options, loggerFactory);
    }

    /// <summary>创建 S7 客户端，传输由调用方提供的通道创建器创建（自定义传输）。</summary>
    /// <param name="transport">传输配置，精确类型必须是 <paramref name="transportCreator"/> 的 <c>ConfigType</c>。</param>
    /// <param name="transportCreator">通道创建器；只有字节流交付的创建器可用（P4-D10）。</param>
    /// <param name="options">S7 选项。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>尚未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/>、<paramref name="transportCreator"/> 或 <paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">
    /// 传输配置的精确类型与创建器不匹配；创建器交付数据报而不是字节流；或 Rack、槽号、连接类型超出 P4-D19 的范围。
    /// </exception>
    public static S7Client Create(IChannelConfig transport, IChannelCreator transportCreator, S7ClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transportCreator == null)
            throw new ArgumentNullException(nameof(transportCreator));

        return Create(null, transport, transportCreator, options, loggerFactory);
    }

    /// <summary>带名称的创建（供依赖注入的创建器使用）：选项先取快照再创建通道，构造客户端失败时释放已创建的通道。</summary>
    internal static S7Client Create(string? name, IChannelConfig transport, IChannelCreator creator, S7ClientOptions options, ILoggerFactory? loggerFactory)
    {
        S7ProtocolSettings snapshot = S7ProtocolSettings.From(options);
        var session = new S7Session();
        IClientChannel channel = PlcChannelBinding.CreateChannel(
            creator,
            name,
            transport,
            (delivery, medium) => S7ProtocolProfile.Configure(new ChannelComponents(), snapshot, session, delivery),
            loggerFactory);
        try
        {
            return new S7Client(channel, snapshot, options, session, loggerFactory?.CreateLogger<S7Client>());
        }
        catch
        {
            // 客户端构造失败（例如 PlcClientOptions 非法）时，通道没有其他所有者，在此释放；异常原样抛出。
            channel.Dispose();
            throw;
        }
    }
}
