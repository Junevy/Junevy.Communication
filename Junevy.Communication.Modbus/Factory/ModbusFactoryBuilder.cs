using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
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
        private IResponseParser? tcpParser;
        private IResponseParser? rtuParser;
        private IModbusFrameBuilder? frameBuilder;
        private IModbusConnectionManager? connectionManager;
        private readonly List<IModbusClientCreator> creators = new();

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

        /// <summary>Sets a custom TCP response parser. Defaults to a new <c>TcpProtocolParser</c>.</summary>
        public ModbusFactoryBuilder WithTcpParser(IResponseParser tcpParser)
        {
            this.tcpParser = tcpParser ?? throw new ArgumentNullException(nameof(tcpParser));
            return this;
        }

        /// <summary>Sets a custom RTU response parser. Defaults to a new <c>RtuProtocolParser</c>.</summary>
        public ModbusFactoryBuilder WithRtuParser(IResponseParser rtuParser)
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
        /// Registers a custom client creation strategy. Creators added here are appended after the
        /// built-in ones, so a creator with the same <see cref="IModbusClientCreator.ConfigType"/>
        /// overrides the built-in creator (later registration wins).
        /// </summary>
        public ModbusFactoryBuilder WithCreator(IModbusClientCreator creator)
        {
            this.creators.Add(creator ?? throw new ArgumentNullException(nameof(creator)));
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
            var allCreators = new List<IModbusClientCreator>(
                ModbusFactory.CreateDefaultCreators(loggers, tcpParser, rtuParser, frameBuilder));
            allCreators.AddRange(creators);

            return new ModbusFactory(
                loggers.CreateLogger<ModbusFactory>(),
                allCreators,
                connectionManager ?? new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance));
        }
    }
}