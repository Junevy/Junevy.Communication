using Junevy.Communication.Modbus.Core;
using Junevy.Communication.Modbus.Factory;
using Junevy.Communication.Modbus.Rtu;
using Junevy.Communication.Modbus.Tcp;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Junevy.Communication.Modbus.DependencyInjection;
using Moq;

namespace Junevy.Communication.Modbus.Tests
{
    public class ModbusFactoryTests
    {
        // ── Singleton lifecycle ──────────────────

        [Fact]
        public void AddModbusFactory_RegistersAsSingleton()
        {
            var services = new ServiceCollection();
            services.AddModbusFactory();
            var provider = services.BuildServiceProvider();

            var factory1 = provider.GetRequiredService<IModbusFactory>();
            var factory2 = provider.GetRequiredService<IModbusFactory>();

            Assert.Same(factory1, factory2);
        }

        [Fact]
        public void AddModbusFactory_FactoryAndConnectionManagerShareRegistry()
        {
            var services = new ServiceCollection();
            services.AddModbusFactory();
            using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IModbusFactory>();
            var manager = provider.GetRequiredService<IModbusConnectionManager>();

            factory.TryAdd("shared", new ModbusTcpClientConfig(), out _);

            Assert.True(manager.TryGet("shared", out var resolved));
            Assert.NotNull(resolved);
        }

        [Fact]
        public void Factory_Dispose_ClearsAllInstances()
        {
            var factory = new ModbusFactory();
            factory.TryAdd("t1", new ModbusTcpClientConfig(), out var _);
            factory.TryAdd("rtu", new ModbusRtuClientConfig { PortName = "COM99" }, out var _);
            Assert.Equal(2, factory.Count);

            factory.Dispose();
            Assert.Throws<ObjectDisposedException>(() => factory.Get("t1"));
            Assert.Equal(0, factory.Count);
        }

        [Fact]
        public async Task Factory_DisposeAsync_ClearsAllInstances()
        {
            var factory = new ModbusFactory();
            factory.TryAdd("t1", new ModbusTcpClientConfig(), out var _);
            await factory.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => factory.Get("t1"));
        }

        // ── Configuration validation ─────────────

        [Fact]
        public void TryAdd_NullConfig_ThrowsArgumentNullException()
        {
            var factory = new ModbusFactory();
            Assert.Throws<ArgumentNullException>(() => factory.TryAdd("key", (ModbusTcpClientConfig)null!, out _));
        }

        [Fact]
        public void TryAdd_EmptyKey_ThrowsArgumentException()
        {
            var factory = new ModbusFactory();
            Assert.Throws<ArgumentException>(() => factory.TryAdd("", new ModbusTcpClientConfig(), out _));
        }

        [Fact]
        public void TryAdd_RTU_EmptyPortName_ThrowsArgumentException()
        {
            var factory = new ModbusFactory();
            Assert.Throws<ArgumentException>(() =>
                factory.TryAdd("key", new ModbusRtuClientConfig { PortName = "" }, out _));
        }

        [Fact]
        public void TryAdd_FillsDefaultValues()
        {
            var factory = new ModbusFactory();
            var config = new ModbusTcpClientConfig { ReadTimeout = 0, WriteTimeout = 0, ConnectTimeout = 0 };
            factory.TryAdd("key", config, out var modbus);

            Assert.NotNull(modbus);
            Assert.Equal(2000, config.ReadTimeout);
            Assert.Equal(2000, config.WriteTimeout);
            Assert.Equal(2000, config.ConnectTimeout);
        }

        // ── CRUD operations ──────────────────────

        [Fact]
        public void TryAdd_DuplicateKey_ReturnsFalse()
        {
            var factory = new ModbusFactory();
            Assert.True(factory.TryAdd("dup", new ModbusTcpClientConfig(), out var m1));
            Assert.False(factory.TryAdd("dup", new ModbusTcpClientConfig(), out var m2));
            Assert.Null(m2);
        }

        [Fact]
        public void TryGet_ExistingKey_ReturnsInstance()
        {
            var factory = new ModbusFactory();
            factory.TryAdd("k", new ModbusTcpClientConfig(), out var added);

            Assert.True(factory.TryGet("k", out var retrieved));
            Assert.Same(added, retrieved);
        }

        [Fact]
        public void TryGet_MissingKey_ReturnsFalse()
        {
            var factory = new ModbusFactory();
            Assert.False(factory.TryGet("nonexistent", out var modbus));
            Assert.Null(modbus);
        }

        [Fact]
        public void TryRemove_RemovesAndDisposes()
        {
            var factory = new ModbusFactory();
            factory.TryAdd("k", new ModbusTcpClientConfig(), out var _);
            Assert.True(factory.TryRemove("k"));
            Assert.False(factory.TryGet("k", out _));
        }

        [Fact]
        public void GetRequired_WrongType_ThrowsInvalidOperation()
        {
            var factory = new ModbusFactory();
            factory.TryAdd("tcp", new ModbusTcpClientConfig(), out _);

            Assert.Throws<InvalidOperationException>(() => factory.GetRequired<ModbusRtuClient>("tcp"));
        }

        [Fact]
        public void Keys_ReturnsAllKeys()
        {
            var factory = new ModbusFactory();
            factory.TryAdd("a", new ModbusTcpClientConfig(), out _);
            factory.TryAdd("b", new ModbusRtuClientConfig { PortName = "COM99" }, out _);

            var keys = factory.Keys.ToList();
            Assert.Contains("a", keys);
            Assert.Contains("b", keys);
        }

        // ── Get / GetOrAdd ───────────────────────

        [Fact]
        public void Get_ReturnsNullForEmptyKey()
        {
            var factory = new ModbusFactory();
            Assert.Null(factory.Get(""));
            Assert.Null(factory.Get(null!));
        }

        [Fact]
        public void GetOrAdd_Idempotent_ReturnsSameInstance()
        {
            var factory = new ModbusFactory();
            var a = factory.GetOrAdd("x", new ModbusTcpClientConfig());
            var b = factory.GetOrAdd("x", new ModbusTcpClientConfig());
            Assert.Same(a, b);
            Assert.Equal(1, factory.Count);
        }

        // ── GetOrAdd concurrency race ────────────

        [Fact]
        public void GetOrAdd_ConcurrentRace_DisposesLosingInstances()
        {
            var manager = new ModbusConnectionManager();
            var created = new List<IModbus>();
            var sync = new object();

            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                manager.GetOrAdd("race", key =>
                {
                    var mock = new Mock<IModbus>();
                    lock (sync) { created.Add(mock.Object); }
                    return mock.Object;
                }))).ToArray();

            Task.WaitAll(tasks);
            var winner = tasks[0].Result;

            Assert.All(tasks, t => Assert.Same(winner, t.Result));
            foreach (var instance in created)
            {
                if (!ReferenceEquals(instance, winner))
                    Mock.Get(instance).Verify(m => m.Dispose(), Times.Once);
            }
            Assert.Equal(1, manager.Count);
        }

        // ── Concurrency smoke test ───────────────

        [Fact]
        public void ConcurrentAddRemove_IsThreadSafe()
        {
            var factory = new ModbusFactory();
            var keys = Enumerable.Range(0, 50).Select(i => $"k{i}").ToList();
            var barrier = new Barrier(keys.Count);
            var errors = 0;

            Parallel.ForEach(keys, key =>
            {
                try
                {
                    barrier.SignalAndWait();
                    factory.GetOrAdd(key, new ModbusTcpClientConfig());
                    factory.TryGet(key, out _);
                    factory.TryRemove(key);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            });

            Assert.Equal(0, errors);
        }

        // ── Builder ──────────────────────────────

        [Fact]
        public void Builder_Defaults_CreatesFunctionalFactory()
        {
            var factory = ModbusFactoryBuilder.Create().Build();

            var modbus = factory.GetOrAdd("b1", new ModbusTcpClientConfig());
            Assert.Same(modbus, factory.Get("b1"));
            Assert.Equal(1, factory.Count);
        }

        [Fact]
        public void Builder_Build_Twice_ReturnsIndependentFactories()
        {
            var builder = ModbusFactoryBuilder.Create();
            var first = builder.Build();
            var second = builder.Build();

            Assert.NotSame(first, second);
            first.TryAdd("k", new ModbusTcpClientConfig(), out _);
            Assert.False(second.TryGet("k", out _));
        }

        [Fact]
        public void Builder_WithConnectionManager_SharesRegistryWithFactory()
        {
            var manager = new ModbusConnectionManager();
            var factory = ModbusFactoryBuilder.Create().WithConnectionManager(manager).Build();

            factory.TryAdd("shared", new ModbusTcpClientConfig(), out _);

            Assert.True(manager.TryGet("shared", out var resolved));
            Assert.NotNull(resolved);
        }

        [Fact]
        public void Builder_WithLoggerFactory_ClientsUseProvidedLoggers()
        {
            var requested = new List<string>();
            var loggerFactory = new SpyLoggerFactory(requested);

            var factory = ModbusFactoryBuilder.Create().WithLoggerFactory(loggerFactory).Build();
            factory.GetOrAdd("k", new ModbusTcpClientConfig());

            Assert.Contains(typeof(ModbusTcpClient).FullName, requested);
        }

        private sealed class SpyLoggerFactory : ILoggerFactory
        {
            private readonly List<string> requested;

            public SpyLoggerFactory(List<string> requested) => this.requested = requested;

            public ILogger CreateLogger(string categoryName)
            {
                requested.Add(categoryName);
                return NullLogger.Instance;
            }

            public void AddProvider(ILoggerProvider provider)
            {
            }

            public void Dispose()
            {
            }
        }

        [Fact]
        public void Builder_NullArguments_ThrowArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => ModbusFactoryBuilder.Create().WithLoggerFactory(null!));
            Assert.Throws<ArgumentNullException>(() => ModbusFactoryBuilder.Create().WithTcpParser(null!));
            Assert.Throws<ArgumentNullException>(() => ModbusFactoryBuilder.Create().WithRtuParser(null!));
            Assert.Throws<ArgumentNullException>(() => ModbusFactoryBuilder.Create().WithFrameBuilder(null!));
            Assert.Throws<ArgumentNullException>(() => ModbusFactoryBuilder.Create().WithConnectionManager(null!));
        }
    }
}
