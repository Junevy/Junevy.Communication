using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>
    /// Fluent builder for constructing a <see cref="ModbusFactory"/> without a Microsoft DI container
    /// (e.g. Prism or other third-party containers register the built instance directly via
    /// <c>RegisterInstance</c>). Unset options fall back to the same defaults the
    /// <c>AddModbusFactory</c> DI registration would provide.
    /// </summary>
    public sealed class ModbusFactoryBuilder
    {
        private ILoggerFactory? loggerFactory;
        private TcpProtocolParser? tcpParser;
        private RtuProtocolParser? rtuParser;
        private IModbusFrameBuilder? frameBuilder;
        private IModbusConnectionManager? connectionManager;

        private ModbusFactoryBuilder()
        {
        }

        /// <summary>Creates a new builder instance.</summary>
        public static ModbusFactoryBuilder Create() => new();

        /// <summary>
        /// Sets the logger factory used by the factory itself and every client it creates
        /// (e.g. a Serilog-backed <see cref="LoggerFactory"/>). Defaults to <see cref="NullLoggerFactory.Instance"/>.
        /// </summary>
        public ModbusFactoryBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return this;
        }

        /// <summary>Sets a custom TCP response parser. Defaults to a new <see cref="TcpProtocolParser"/>.</summary>
        public ModbusFactoryBuilder WithTcpParser(TcpProtocolParser tcpParser)
        {
            this.tcpParser = tcpParser ?? throw new ArgumentNullException(nameof(tcpParser));
            return this;
        }

        /// <summary>Sets a custom RTU response parser. Defaults to a new <see cref="RtuProtocolParser"/>.</summary>
        public ModbusFactoryBuilder WithRtuParser(RtuProtocolParser rtuParser)
        {
            this.rtuParser = rtuParser ?? throw new ArgumentNullException(nameof(rtuParser));
            return this;
        }

        /// <summary>Sets a custom request frame builder. Defaults to a new <see cref="ModbusFrameBuilder"/>.</summary>
        public ModbusFactoryBuilder WithFrameBuilder(IModbusFrameBuilder frameBuilder)
        {
            this.frameBuilder = frameBuilder ?? throw new ArgumentNullException(nameof(frameBuilder));
            return this;
        }

        /// <summary>
        /// Sets the connection manager that owns the named instances. Pass a standalone
        /// <see cref="ModbusConnectionManager"/> to share its registry with the host container;
        /// defaults to a new manager owned by the built factory.
        /// </summary>
        public ModbusFactoryBuilder WithConnectionManager(IModbusConnectionManager connectionManager)
        {
            this.connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            return this;
        }

        /// <summary>Builds the factory. Each call returns a new, independent instance.</summary>
        public ModbusFactory Build()
        {
            var loggers = loggerFactory ?? NullLoggerFactory.Instance;
            return new ModbusFactory(
                loggers.CreateLogger<ModbusFactory>(),
                loggers,
                tcpParser,
                rtuParser,
                frameBuilder,
                connectionManager);
        }
    }
}
