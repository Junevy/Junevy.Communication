using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.RTU;
using Junevy.Communication.Modbus.TCP;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>
    /// Creates and manages named Modbus instances.
    /// Delegates lifecycle management to <see cref="ModbusConnectionManager"/> and
    /// protocol-specific response parsing to <see cref="TcpProtocolParser"/> / <see cref="RtuProtocolParser"/>.
    /// </summary>
    public sealed class ModbusFactory : IModbusFactory
    {
        private readonly IModbusConnectionManager manager;
        private readonly ILogger<ModbusFactory> logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly TcpProtocolParser tcpParser;
        private readonly RtuProtocolParser rtuParser;
        private readonly IModbusFrameBuilder frameBuilder;
        private bool disposed;

        public int Count => manager.Count;
        public IEnumerable<string> Keys => manager.Keys;

        public ModbusFactory()
            : this(NullLogger<ModbusFactory>.Instance, NullLoggerFactory.Instance)
        {
        }

        public ModbusFactory(ILogger<ModbusFactory> logger, ILoggerFactory loggerFactory)
            : this(logger, loggerFactory, null, null)
        {
        }

        public ModbusFactory(
            ILogger<ModbusFactory> logger,
            ILoggerFactory loggerFactory,
            TcpProtocolParser? tcpParser,
            RtuProtocolParser? rtuParser)
            : this(
                logger,
                loggerFactory,
                tcpParser,
                rtuParser,
                null,
                new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance))
        {
        }

        public ModbusFactory(
            ILogger<ModbusFactory> logger,
            ILoggerFactory loggerFactory,
            TcpProtocolParser tcpParser,
            RtuProtocolParser rtuParser,
            IModbusFrameBuilder frameBuilder)
            : this(
                logger,
                loggerFactory,
                tcpParser,
                rtuParser,
                frameBuilder,
                new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance))
        {
        }

        public ModbusFactory(
            ILogger<ModbusFactory> logger,
            ILoggerFactory loggerFactory,
            TcpProtocolParser? tcpParser,
            RtuProtocolParser? rtuParser,
            IModbusFrameBuilder? frameBuilder,
            IModbusConnectionManager manager)
        {
            this.logger = logger ?? NullLogger<ModbusFactory>.Instance;
            this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            this.tcpParser = tcpParser ?? new TcpProtocolParser();
            this.rtuParser = rtuParser ?? new RtuProtocolParser();
            this.frameBuilder = frameBuilder ?? new ModbusFrameBuilder();
            this.manager = manager ?? new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance);
        }

        public IModbus? Get(string key)
        {
            ThrowIfDisposed();
            return manager.Get(key);
        }

        public TResult GetRequired<TResult>(string key) where TResult : class, IModbus
        {
            return manager.GetRequired<TResult>(key);
        }

        public bool TryGet(string key, out IModbus? modbus)
        {
            ThrowIfDisposed();
            return manager.TryGet(key, out modbus);
        }

        public IModbus GetOrAdd(string key, ModbusTCPConfig config)
        {
            ThrowIfDisposed();
            ValidateAndFillDefaults(config, key);
            logger.LogInformation(" [GetOrAdd] TCP: key={Key}, address={Address}:{Port}.", key, config.Address, config.Port);

            return manager.GetOrAdd(key, _ =>
            {
                var tcp = new ModbusTCP(config, loggerFactory.CreateLogger<ModbusTCP>(), tcpParser, frameBuilder);
                logger.LogDebug(" [GetOrAdd] Created ModbusTCP: key={Key}.", key);
                return tcp;
            });
        }

        public IModbus GetOrAdd(string key, ModbusRTUConfig config)
        {
            ThrowIfDisposed();
            ValidateAndFillDefaults(config, key);
            logger.LogInformation(" [GetOrAdd] RTU: key={Key}, port={PortName}.", key, config.PortName);

            return manager.GetOrAdd(key, _ =>
            {
                var rtu = new ModbusRTU(config, loggerFactory.CreateLogger<ModbusRTU>(), rtuParser, frameBuilder);
                logger.LogDebug(" [GetOrAdd] Created ModbusRTU: key={Key}.", key);
                return rtu;
            });
        }

        public bool TryAdd(string key, ModbusTCPConfig config, out IModbus? modbus)
        {
            ThrowIfDisposed();
            modbus = null;
            ValidateAndFillDefaults(config, key);
            logger.LogInformation(" [TryAdd] TCP: key={Key}, address={Address}:{Port}.", key, config.Address, config.Port);

            var tcp = new ModbusTCP(config, loggerFactory.CreateLogger<ModbusTCP>(), tcpParser, frameBuilder);
            if (!manager.Add(key, tcp))
            {
                tcp.Dispose();
                logger.LogWarning(" [TryAdd] TCP failed: key '{Key}' already exists.", key);
                return false;
            }

            modbus = tcp;
            logger.LogDebug(" [TryAdd] Added ModbusTCP: key={Key}.", key);
            return true;
        }

        public bool TryAdd(string key, ModbusRTUConfig config, out IModbus? modbus)
        {
            ThrowIfDisposed();
            modbus = null;
            ValidateAndFillDefaults(config, key);
            logger.LogInformation(" [TryAdd] RTU: key={Key}, port={PortName}.", key, config.PortName);

            var rtu = new ModbusRTU(config, loggerFactory.CreateLogger<ModbusRTU>(), rtuParser, frameBuilder);
            if (!manager.Add(key, rtu))
            {
                rtu.Dispose();
                logger.LogWarning(" [TryAdd] RTU failed: key '{Key}' already exists.", key);
                return false;
            }

            modbus = rtu;
            logger.LogDebug(" [TryAdd] Added ModbusRTU: key={Key}.", key);
            return true;
        }

        public bool TryRemove(string key)
        {
            ThrowIfDisposed();
            return manager.TryRemove(key);
        }

        public bool RegisterAlias(string aliasKey, string existingKey)
        {
            ThrowIfDisposed();
            return manager.RegisterAlias(aliasKey, existingKey);
        }

        public bool RemoveAlias(string aliasKey)
        {
            ThrowIfDisposed();
            return manager.RemoveAlias(aliasKey);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            int count = manager.Count;
            manager.Dispose();
            logger.LogInformation(" [Dispose] ModbusFactory disposed ({Count} instances).", count);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;

            int count = manager.Count;
            if (manager is IAsyncDisposable ad)
                await ad.DisposeAsync();
            else
                manager.Dispose();
            logger.LogInformation(" [DisposeAsync] ModbusFactory disposed ({Count} instances).", count);
        }

        private static void ValidateAndFillDefaults(ModbusTCPConfig config, string key)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            if (string.IsNullOrEmpty(config.Address))
                config.Address = "127.0.0.1";
            if (config.Port == 0)
                config.Port = 502;
            if (config.ReadTimeOut <= 0)
                config.ReadTimeOut = 2000;
            if (config.WriteTimeOut <= 0)
                config.WriteTimeOut = 2000;
            if (config.ConnectTimeout <= 0)
                config.ConnectTimeout = 2000;
            if (config.RetryCount < 0)
                config.RetryCount = 0;
            if (config.RetryInterval < 0)
                config.RetryInterval = 100;
        }

        private static void ValidateAndFillDefaults(ModbusRTUConfig config, string key)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            if (string.IsNullOrEmpty(config.PortName))
                throw new ModbusException(ModbusErrorCode.GatewayUnavailable,
                    "PortName must not be null or empty.");
            if (config.BaudRate <= 0)
                config.BaudRate = 9600;
            if (config.DataBits < 5 || config.DataBits > 8)
                config.DataBits = 8;
            if (config.ReadTimeOut <= 0)
                config.ReadTimeOut = 2000;
            if (config.WriteTimeOut <= 0)
                config.WriteTimeOut = 2000;
            if (config.RetryCount < 0)
                config.RetryCount = 0;
            if (config.RetryInterval < 0)
                config.RetryInterval = 100;
            if (config.IntervalTime < 0)
                config.IntervalTime = 30;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ModbusFactory));
        }
    }
}
