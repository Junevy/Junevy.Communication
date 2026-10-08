using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Rtu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>RTU（Modbus RTU over 串口）传输的创建策略。</summary>
    public sealed class RtuClientCreator : IModbusClientCreator
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly IResponseParser parser;
        private readonly IModbusFrameBuilder frameBuilder;

        public RtuClientCreator(
            ILoggerFactory? loggerFactory = null,
            IResponseParser? parser = null,
            IModbusFrameBuilder? frameBuilder = null)
        {
            this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            this.parser = parser ?? new Core.Parsing.RtuProtocolParser();
            this.frameBuilder = frameBuilder ?? new ModbusFrameBuilder();
        }

        public Type ConfigType => typeof(ModbusRtuClientConfig);

        public void Normalize(IModbusConfig config)
        {
            var rtu = (ModbusRtuClientConfig)config;
            if (string.IsNullOrEmpty(rtu.PortName))
                throw new ArgumentException("PortName must not be null or empty.", nameof(config));
            if (rtu.BaudRate <= 0)
                rtu.BaudRate = 9600;
            if (rtu.DataBits < 5 || rtu.DataBits > 8)
                rtu.DataBits = 8;
            if (rtu.ReadTimeout <= 0)
                rtu.ReadTimeout = 2000;
            if (rtu.WriteTimeout <= 0)
                rtu.WriteTimeout = 2000;
            if (rtu.RetryCount < 0)
                rtu.RetryCount = 0;
            if (rtu.RetryInterval < 0)
                rtu.RetryInterval = 100;
            if (rtu.FrameReadInterval < 0)
                rtu.FrameReadInterval = 30;
        }

        public IModbus Create(IModbusConfig config)
            => new ModbusRtuClient(
                (ModbusRtuClientConfig)config,
                loggerFactory.CreateLogger<ModbusRtuClient>(),
                parser,
                frameBuilder);

        public string Describe(IModbusConfig config)
            => ((ModbusRtuClientConfig)config).PortName;
    }
}