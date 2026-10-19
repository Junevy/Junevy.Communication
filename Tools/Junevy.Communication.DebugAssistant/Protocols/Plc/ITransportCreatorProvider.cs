using Junevy.Communication.Channels;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>PLC 页签可用的传输方式。</summary>
public enum PlcTransport
{
    Tcp,
    Udp,
    Serial,
}

/// <summary>
/// 提供协议包的 <c>Create(transport, transportCreator, options, loggerFactory)</c> 重载所需的通道创建器（计划 Task 7 的测试接缝）。
/// 正式运行时返回库内置的创建器；测试时替换为返回 Testing 库假通道的创建器，不连接真实设备。
/// </summary>
public interface ITransportCreatorProvider
{
    IChannelCreator Get(PlcTransport transport);
}

/// <summary>正式运行时的实现：库内置的 TCP、UDP 与串口创建器。</summary>
internal sealed class TransportCreatorProvider : ITransportCreatorProvider
{
    private readonly TcpClientChannelCreator _tcp = new();
    private readonly UdpChannelCreator _udp = new();
    private readonly SerialChannelCreator _serial = new();

    public IChannelCreator Get(PlcTransport transport) => transport switch
    {
        PlcTransport.Tcp => _tcp,
        PlcTransport.Udp => _udp,
        PlcTransport.Serial => _serial,
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
    };
}
