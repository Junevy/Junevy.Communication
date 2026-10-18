using Junevy.Communication.Channels;
using Junevy.Communication.Core.Registry;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 客户端（主站）：TCP、UDP 与串口上的 MBAP 或 RTU 帧。地址写作 <c>[s=站号;]存储区编号[.位号]</c>，
/// 例如 <c>HR100</c>、<c>40001</c>、<c>s=2;HR100.5</c>。公开操作的失败通过 <c>CommResult</c> 返回，不抛出运行期异常。
/// </summary>
/// <remarks>
/// 同一实例上的操作串行执行（包括拆包的全部分包）。一包的期望在构造请求时记录，解析应答时使用，两者之间由操作锁保护。
/// 没有同步 API（设计 21.1 Q2）。
/// </remarks>
public sealed partial class ModbusClient : PlcClientBase<ModbusAddress>
{
    internal static readonly CreatorCatalog<IChannelCreator> BuiltInCreators = new CreatorCatalog<IChannelCreator>(new IChannelCreator[]
    {
        new TcpClientChannelCreator(),
        new UdpChannelCreator(),
        new SerialChannelCreator(),
    });

    private readonly ModbusProtocolSettings settings;
    private readonly ModbusFraming framing;

    // 当前一包的期望：构造请求时记录（每次尝试都重新记录），解析同一包的应答时取出。操作锁保证两者之间没有其他操作。
    private ModbusExpectation? pending;

    internal ModbusClient(IClientChannel channel, ModbusProtocolSettings settings, ModbusFraming framing, ModbusClientOptions options, ILogger? logger)
        : base(channel, options, settings.DataFormat, logger ?? NullLogger.Instance)
    {
        this.settings = settings;
        this.framing = framing;
    }

    /// <summary>
    /// 创建客户端：按传输配置的类型选择内置的创建器（TCP、UDP 或串口）。创建不连接，连接由 <see cref="PlcClientBase{TAddress}.ConnectAsync"/> 完成。
    /// </summary>
    /// <param name="transport">传输配置。</param>
    /// <param name="options">Modbus 选项（取快照）。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不记录日志。</param>
    /// <returns>新的客户端，拥有其通道。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> 或 <paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">选项与介质不符（例如串口上的 MBAP），或传输配置没有内置的创建器。</exception>
    /// <exception cref="ArgumentOutOfRangeException">选项中的帧格式、站号等未定义或超出范围。</exception>
    public static ModbusClient Create(IChannelConfig transport, ModbusClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transport is null)
            throw new ArgumentNullException(nameof(transport));

        return Create(null, transport, BuiltInCreators.Resolve(transport), options, loggerFactory);
    }

    /// <summary>
    /// 使用指定的创建器创建客户端，适用于自定义的通道实现。
    /// </summary>
    /// <param name="transport">传输配置。</param>
    /// <param name="transportCreator">通道创建器。</param>
    /// <param name="options">Modbus 选项（取快照）。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不记录日志。</param>
    /// <returns>新的客户端，拥有其通道。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    /// <exception cref="ArgumentException">选项与介质不符，或传输配置类型与创建器不符。</exception>
    public static ModbusClient Create(IChannelConfig transport, IChannelCreator transportCreator, ModbusClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (transportCreator is null)
            throw new ArgumentNullException(nameof(transportCreator));

        return Create(null, transport, transportCreator, options, loggerFactory);
    }

    internal static ModbusClient Create(string? name, IChannelConfig transport, IChannelCreator creator, ModbusClientOptions options, ILoggerFactory? loggerFactory)
    {
        ModbusProtocolSettings snapshot = ModbusProtocolSettings.From(options);
        ModbusFraming resolved = ModbusProtocolProfile.ResolveFraming(snapshot.Framing, creator.Medium);
        IClientChannel channel = PlcChannelBinding.CreateChannel(
            creator,
            name,
            transport,
            (delivery, medium) => ModbusProtocolProfile.Configure(new ChannelComponents(), snapshot, delivery, medium),
            loggerFactory);

        try
        {
            return new ModbusClient(channel, snapshot, resolved, options, loggerFactory?.CreateLogger<ModbusClient>());
        }
        catch
        {
            // 构造失败时，通道没有其他所有者，在此释放；异常原样抛出。
            channel.Dispose();
            throw;
        }
    }

    // 取出当前一包的期望；没有记录就是协议实现错误，由流水线收口为 Unspecified。
    private ModbusExpectation TakeExpectation()
    {
        ModbusExpectation? expectation = pending;
        pending = null;
        return expectation ?? throw new InvalidOperationException("No request expectation was recorded for this response.");
    }

    private void Expect(ModbusExpectation expectation) => pending = expectation;
}
