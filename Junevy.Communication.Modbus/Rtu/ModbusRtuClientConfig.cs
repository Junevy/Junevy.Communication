using Junevy.Communication.Modbus.Core.Interfaces;
using System.IO.Ports;

namespace Junevy.Communication.Modbus.Rtu
{
    public class ModbusRtuClientConfig : IModbusConfig
    {
        /// <summary>
        /// Serial port name (e.g., COM1, /dev/ttyUSB0).
        /// </summary>
        public string PortName { get; set; } = "COM20";

        /// <summary>
        /// Baud rate.
        /// </summary>
        public int BaudRate { get; set; } = 9600;

        /// <summary>
        /// Parity setting.
        /// </summary>
        public Parity Parity { get; set; } = Parity.None;

        /// <summary>
        /// Data bits (5-8).
        /// </summary>
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// Stop bits.
        /// </summary>
        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>
        /// Enable DTR signal.
        /// </summary>
        public bool DtrEnable { get; set; } = false;

        /// <summary>
        /// Enable RTS signal.
        /// </summary>
        public bool RtsEnable { get; set; } = false;

        /// <summary>
        /// 连接被销毁后（超时、对端关闭、发送失败）是否在下一次尝试前自动重建连接。
        /// 为 false 时，上述失败发生后请求立即返回，不再重试，需要调用方手动 Connect()。
        /// </summary>
        public bool Reconnect { get; set; } = false;

        /// <summary>
        /// Write timeout in milliseconds.
        /// </summary>
        public int WriteTimeout { get; set; } = 2000;

        /// <summary>
        /// Read timeout in milliseconds.
        /// </summary>
        public int ReadTimeout { get; set; } = 2000;

        /// <summary>
        /// 首次尝试失败后的最大重试次数。TCP 在超时或连接关闭后必须重建连接才能重试，
        /// 因此 Reconnect 为 false 时这类失败不会重试；不需要换连接的失败（例如 RTU 的 CRC 错误）按此次数重试。
        /// </summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>
        /// Delay between retry/reconnect attempts in milliseconds.
        /// </summary>
        public int RetryInterval { get; set; } = 100;

        /// <summary>
        /// Interval in milliseconds to wait between partial frame reads.
        /// </summary>
        public int FrameReadInterval { get; set; } = 30;
    }
}
