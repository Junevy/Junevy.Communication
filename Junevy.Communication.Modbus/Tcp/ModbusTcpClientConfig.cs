using Junevy.Communication.Modbus.Core.Interfaces;

namespace Junevy.Communication.Modbus.Tcp
{
    public class ModbusTcpClientConfig : IModbusConfig
    {
        public string Address { get; set; } = "127.0.0.1";

        /// <summary>TCP 端口（1..65535，Modbus 标准端口 502）。</summary>
        public int Port { get; set; } = 502;

        /// <summary>
        /// 连接被销毁后（超时、对端关闭、发送失败）是否在下一次尝试前自动重建连接。
        /// 为 false 时，上述失败发生后请求立即返回，不再重试，需要调用方手动 Connect()。
        /// </summary>
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
    }
}
