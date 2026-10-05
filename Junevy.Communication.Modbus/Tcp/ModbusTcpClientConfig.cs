using Junevy.Communication.Modbus.Core.Interfaces;

namespace Junevy.Communication.Modbus.Tcp
{
    public class ModbusTcpClientConfig : IModbusConfig
    {
        public string Address { get; set; } = "127.0.0.1";

        /// <summary>TCP 端口（1..65535，Modbus 标准端口 502）。</summary>
        public int Port { get; set; } = 502;

        public bool Reconnect { get; set; } = false;

        /// <summary>
        /// Delay between retry/reconnect attempts in milliseconds.
        /// </summary>
        public int RetryInterval { get; set; } = 100;

        /// <summary>
        /// Connection timeout in milliseconds.
        /// </summary>
        public int ConnectTimeout { get; set; } = 2000;

        /// <summary>
        /// Write timeout in milliseconds.
        /// </summary>
        public int WriteTimeOut { get; set; } = 2000;

        /// <summary>
        /// Read timeout in milliseconds.
        /// </summary>
        public int ReadTimeOut { get; set; } = 2000;

        /// <summary>
        /// Number of retry attempts.
        /// </summary>
        public int RetryCount { get; set; } = 3;
    }
}
