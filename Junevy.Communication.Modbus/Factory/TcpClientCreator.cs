using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Tcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>TCP（Modbus TCP）传输的创建策略。</summary>
    public sealed class TcpClientCreator : IModbusClientCreator
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly IResponseParser parser;
        private readonly IModbusFrameBuilder frameBuilder;

        public TcpClientCreator(
            ILoggerFactory? loggerFactory = null,
            IResponseParser? parser = null,
            IModbusFrameBuilder? frameBuilder = null)
        {
            this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            this.parser = parser ?? new Core.Parsing.TcpProtocolParser();
            this.frameBuilder = frameBuilder ?? new ModbusFrameBuilder();
        }

        public Type ConfigType => typeof(ModbusTcpClientConfig);

        public void Normalize(IModbusConfig config)
        {
            var tcp = (ModbusTcpClientConfig)config;
            if (string.IsNullOrEmpty(tcp.Address))
                tcp.Address = "127.0.0.1";
            if (tcp.Port == 0)
                tcp.Port = 502;
            if (tcp.ReadTimeout <= 0)
                tcp.ReadTimeout = 2000;
            if (tcp.WriteTimeout <= 0)
                tcp.WriteTimeout = 2000;
            if (tcp.ConnectTimeout <= 0)
                tcp.ConnectTimeout = 2000;
            if (tcp.RetryCount < 0)
                tcp.RetryCount = 0;
            if (tcp.RetryInterval < 0)
                tcp.RetryInterval = 100;
        }

        public IModbus Create(IModbusConfig config)
            => new ModbusTcpClient(
                (ModbusTcpClientConfig)config,
                loggerFactory.CreateLogger<ModbusTcpClient>(),
                parser,
                frameBuilder);

        public string Describe(IModbusConfig config)
        {
            var tcp = (ModbusTcpClientConfig)config;
            return $"{tcp.Address}:{tcp.Port}";
        }
    }
}