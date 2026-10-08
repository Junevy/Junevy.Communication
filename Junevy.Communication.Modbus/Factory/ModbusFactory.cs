using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Core.Framing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>
    /// Creates and manages named Modbus instances. Client creation is delegated to
    /// <see cref="IModbusClientCreator"/> strategies resolved by configuration type.
    /// </summary>
    public sealed class ModbusFactory : IModbusFactory
    {
        private readonly IModbusConnectionManager manager;
        private readonly ILogger<ModbusFactory> logger;
        private readonly Dictionary<Type, IModbusClientCreator> creators;
        private bool disposed;

        public int Count => manager.Count;
        public IEnumerable<string> Keys => manager.Keys;

        public ModbusFactory()
            : this(
                NullLogger<ModbusFactory>.Instance,
                CreateDefaultCreators(NullLoggerFactory.Instance, null, null, null),
                new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance))
        {
        }

        public ModbusFactory(
            ILogger<ModbusFactory> logger,
            IEnumerable<IModbusClientCreator> creators,
            IModbusConnectionManager manager)
        {
            this.logger = logger ?? NullLogger<ModbusFactory>.Instance;
            this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
            if (creators == null)
                throw new ArgumentNullException(nameof(creators));

            // 同一 ConfigType 出现多次时，枚举顺序靠后的覆盖靠前的（与 Microsoft DI 的"最后注册者生效"一致）
            this.creators = new Dictionary<Type, IModbusClientCreator>();
            foreach (var creator in creators)
                this.creators[creator.ConfigType] = creator;
        }

        /// <summary>创建 TCP 与 RTU 的内置创建器。</summary>
        internal static IReadOnlyList<IModbusClientCreator> CreateDefaultCreators(
            ILoggerFactory loggerFactory,
            IResponseParser? tcpParser,
            IResponseParser? rtuParser,
            IModbusFrameBuilder? frameBuilder)
        {
            return new IModbusClientCreator[]
            {
                new TcpClientCreator(loggerFactory, tcpParser ?? new TcpProtocolParser(), frameBuilder),
                new RtuClientCreator(loggerFactory, rtuParser ?? new RtuProtocolParser(), frameBuilder)
            };
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

        public IModbus GetOrAdd(string key, IModbusConfig config)
        {
            ThrowIfDisposed();
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));

            var creator = ResolveCreator(config);
            creator.Normalize(config);
            logger.LogInformation(" [GetOrAdd] key={Key}, target={Target}.", key, creator.Describe(config));

            var result = manager.GetOrAdd(key, _ => creator.Create(config));
            logger.LogDebug(" [GetOrAdd] Created {ClientType}: key={Key}.", result.GetType().Name, key);
            return result;
        }

        public bool TryAdd(string key, IModbusConfig config, out IModbus? modbus)
        {
            ThrowIfDisposed();
            modbus = null;
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));

            var creator = ResolveCreator(config);
            creator.Normalize(config);
            logger.LogInformation(" [TryAdd] key={Key}, target={Target}.", key, creator.Describe(config));

            var created = creator.Create(config);
            if (!manager.Add(key, created))
            {
                created.Dispose();
                logger.LogWarning(" [TryAdd] failed: key '{Key}' already exists.", key);
                return false;
            }

            modbus = created;
            logger.LogDebug(" [TryAdd] Added {ClientType}: key={Key}.", created.GetType().Name, key);
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

        /// <summary>
        /// 从 config 的精确类型开始沿 BaseType 链向上查找第一个已注册的 ConfigType；
        /// 找不到抛 NotSupportedException（消息含配置类型全名）。
        /// </summary>
        private IModbusClientCreator ResolveCreator(IModbusConfig config)
        {
            for (Type? type = config.GetType(); type != null; type = type.BaseType)
            {
                if (creators.TryGetValue(type, out var creator))
                    return creator;
            }

            throw new NotSupportedException(
                $"No IModbusClientCreator is registered for configuration type '{config.GetType().FullName}'.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ModbusFactory));
        }
    }
}