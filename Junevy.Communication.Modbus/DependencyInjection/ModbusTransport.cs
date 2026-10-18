namespace Junevy.Communication.Modbus.DependencyInjection;

/// <summary>Modbus 客户端的传输方式（P7-D14）：决定 <see cref="ModbusClientConfig"/> 中使用哪一个传输配置。</summary>
public enum ModbusTransport
{
    /// <summary>TCP（MBAP 或 RTU over TCP）。</summary>
    Tcp,

    /// <summary>UDP（MBAP 或 RTU over UDP）。</summary>
    Udp,

    /// <summary>串口（RTU）。</summary>
    Serial,
}
