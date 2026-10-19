using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Modbus.DependencyInjection;

/// <summary>
/// Modbus 客户端的依赖注入配置（P7-D14）：绑定 appsettings 的形状。<see cref="Transport"/> 选中的传输配置必须不为 null。
/// </summary>
public sealed class ModbusClientConfig : IPlcClientConfig
{
    /// <summary>传输方式，默认 TCP。</summary>
    public ModbusTransport Transport { get; set; } = ModbusTransport.Tcp;

    /// <summary>TCP 的传输配置（<see cref="Transport"/> 为 Tcp 时必需）。</summary>
    public TcpClientChannelConfig? Tcp { get; set; }

    /// <summary>UDP 的传输配置（<see cref="Transport"/> 为 Udp 时必需）。</summary>
    public UdpChannelConfig? Udp { get; set; }

    /// <summary>串口的传输配置（<see cref="Transport"/> 为 Serial 时必需）。</summary>
    public SerialChannelConfig? Serial { get; set; }

    /// <summary>Modbus 选项。</summary>
    public ModbusClientOptions Options { get; set; } = new ModbusClientOptions();
}
