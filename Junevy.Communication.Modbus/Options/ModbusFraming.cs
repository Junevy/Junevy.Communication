namespace Junevy.Communication.Modbus;

/// <summary>Modbus 的帧格式（P7-D7）。</summary>
public enum ModbusFraming
{
    /// <summary>MBAP 头：Modbus TCP 与 Modbus UDP。</summary>
    Mbap,

    /// <summary>RTU 帧：串口，或 RTU over TCP / UDP（透传网关与串口服务器上常用）。</summary>
    Rtu,
}
