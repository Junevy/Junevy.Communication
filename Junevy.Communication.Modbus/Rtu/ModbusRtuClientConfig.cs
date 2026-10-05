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
        /// Reopen the serial port automatically before retrying failed requests.
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
        /// Number of retry attempts.
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
