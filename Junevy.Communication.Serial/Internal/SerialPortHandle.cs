using System.IO.Ports;

namespace Junevy.Communication.Serial.Internal;

/// <summary>
/// 基于 <see cref="SerialPort"/> 的端口实现（内部）。构造时复制端口参数，只在 <see cref="Open"/> 时触及设备。
/// 缓冲区大小必须在打开之前设置（<see cref="SerialPort"/> 的要求），因此在构造时设置。
/// </summary>
internal sealed class SerialPortHandle : ISerialPortHandle
{
    /// <summary>默认的端口工厂：每次创建一个新的 <see cref="SerialPortHandle"/>。</summary>
    internal static readonly ISerialPortHandleFactory DefaultFactory = new Factory();

    private readonly SerialPort port;
    private readonly int writeTimeout;

    /// <summary>
    /// 创建端口对象（尚未打开，不触及设备）。
    /// </summary>
    /// <param name="config">端口参数；构造时读取，之后不再访问。</param>
    public SerialPortHandle(SerialChannelConfig config)
    {
        port = new SerialPort(config.PortName, config.BaudRate, config.Parity, config.DataBits, config.StopBits)
        {
            Handshake = config.Handshake,
            DtrEnable = config.DtrEnable,
            RtsEnable = config.RtsEnable,
            ReadBufferSize = config.ReadBufferSize,
            WriteBufferSize = config.WriteBufferSize,
        };
        writeTimeout = config.SendTimeout;
    }

    /// <inheritdoc />
    public Stream BaseStream => port.BaseStream;

    /// <summary>
    /// 打开端口，之后设置超时：读取无限等待（帧结束由分帧器与 IdleTimeout 判定）；写出超时为 SendTimeout 的后备（发送计时由 StreamChannel 负责）。
    /// </summary>
    public void Open()
    {
        port.Open();
        port.ReadTimeout = SerialPort.InfiniteTimeout;
        port.WriteTimeout = writeTimeout;
    }

    /// <inheritdoc />
    public void DiscardInBuffer() => port.DiscardInBuffer();

    /// <inheritdoc />
    public void Dispose() => port.Dispose();

    private sealed class Factory : ISerialPortHandleFactory
    {
        public ISerialPortHandle Create(SerialChannelConfig config) => new SerialPortHandle(config);
    }
}
