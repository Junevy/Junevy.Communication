using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.DependencyInjection;
using Junevy.Communication.Modbus.Factory;
using Junevy.Communication.Modbus.Rtu;
using Junevy.Communication.Modbus.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Reflection;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 工厂策略化（IModbusClientCreator）：按配置类型解析创建器，支持自定义传输。
    /// </summary>
    public class FactoryStrategyTests
    {
        // ————————————————— 内置创建器 —————————————————

        [Fact]
        public void GetOrAdd_TcpConfig_CreatesTcpClient()
        {
            using var factory = new ModbusFactory();
            Assert.IsType<ModbusTcpClient>(factory.GetOrAdd("k", new ModbusTcpClientConfig()));
        }

        [Fact]
        public void GetOrAdd_RtuConfig_CreatesRtuClient()
        {
            using var factory = new ModbusFactory();
            Assert.IsType<ModbusRtuClient>(factory.GetOrAdd("k", new ModbusRtuClientConfig { PortName = "COM1" }));
        }

        [Fact]
        public void GetOrAdd_UnknownConfigType_ThrowsNotSupported()
        {
            using var factory = new ModbusFactory();

            var ex = Assert.Throws<NotSupportedException>(() => factory.GetOrAdd("k", new FakeConfig()));
            Assert.Contains("FakeConfig", ex.Message);
        }

        [Fact]
        public void GetOrAdd_DerivedConfig_UsesBaseTypeCreator()
        {
            using var factory = new ModbusFactory();
            Assert.IsType<ModbusTcpClient>(factory.GetOrAdd("k", new DerivedTcpConfig()));
        }

        // ————————————————— 自定义创建器 —————————————————

        [Fact]
        public void GetOrAdd_CustomCreator_IsUsed()
        {
            var creator = new FakeCreator(typeof(FakeConfig));
            using var factory = CreateFactory(creator);
            var config = new FakeConfig();

            var result = factory.GetOrAdd("k", config);

            Assert.IsType<TrackedModbus>(result);
            Assert.Equal(1, creator.NormalizeCalls);
            Assert.Equal(1, creator.CreateCalls);
        }

        [Fact]
        public void GetOrAdd_SameKeyTwice_CreatesOnce_NormalizesTwice()
        {
            var creator = new FakeCreator(typeof(FakeConfig));
            using var factory = CreateFactory(creator);

            var first = factory.GetOrAdd("k", new FakeConfig());
            var second = factory.GetOrAdd("k", new FakeConfig());

            Assert.Equal(1, creator.CreateCalls);
            Assert.Equal(2, creator.NormalizeCalls);
            Assert.Same(first, second);
        }

        [Fact]
        public void Creators_SameConfigType_LaterWins()
        {
            var a = new FakeCreator(typeof(FakeConfig));
            var b = new FakeCreator(typeof(FakeConfig));
            using var factory = CreateFactory(a, b);

            factory.GetOrAdd("k", new FakeConfig());

            Assert.Equal(0, a.CreateCalls);
            Assert.Equal(1, b.CreateCalls);
        }

        [Fact]
        public void Ctor_NullCreators_Throws()
        {
            var manager = new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance);
            Assert.Throws<ArgumentNullException>(
                () => new ModbusFactory(NullLogger<ModbusFactory>.Instance, null!, manager));
        }

        [Fact]
        public void TryAdd_CustomCreator_DuplicateKey_DisposesNewInstance()
        {
            var creator = new FakeCreator(typeof(FakeConfig));
            using var factory = CreateFactory(creator);

            Assert.True(factory.TryAdd("k", new FakeConfig(), out var first));

            Assert.False(factory.TryAdd("k", new FakeConfig(), out var second));
            Assert.Null(second);                                   // 失败时 modbus 为 null
            Assert.Equal(2, creator.Created.Count);
            Assert.Equal(0, ((TrackedModbus)creator.Created[0]).DisposeCount);
            Assert.Equal(1, ((TrackedModbus)creator.Created[1]).DisposeCount);   // 重复键的实例被立即释放
        }

        // ————————————————— Builder —————————————————

        [Fact]
        public void Builder_WithCreator_OverridesDefault()
        {
            var creator = new FakeCreator(typeof(ModbusTcpClientConfig));
            using var factory = ModbusFactoryBuilder.Create().WithCreator(creator).Build();

            var result = factory.GetOrAdd("k", new ModbusTcpClientConfig());
            Assert.IsType<TrackedModbus>(result);
        }

        [Fact]
        public void Builder_WithTcpParser_AcceptsAnyResponseParser()
        {
            IResponseParser custom = Mock.Of<IResponseParser>();
            using var factory = ModbusFactoryBuilder.Create().WithTcpParser(custom).Build();

            Assert.IsType<ModbusTcpClient>(factory.GetOrAdd("k", new ModbusTcpClientConfig()));
        }

        // ————————————————— DI —————————————————

        [Fact]
        public void Di_CustomCreatorRegisteredAfterAddModbusFactory_Wins()
        {
            var creator = new FakeCreator(typeof(ModbusTcpClientConfig));
            var services = new ServiceCollection();
            services.AddModbusFactory();
            services.AddSingleton<IModbusClientCreator>(creator);

            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IModbusFactory>();

            var result = factory.GetOrAdd("k", new ModbusTcpClientConfig());
            Assert.IsType<TrackedModbus>(result);
        }

        [Fact]
        public void Di_DefaultRegistration_CreatesBuiltInClients()
        {
            var services = new ServiceCollection();
            services.AddModbusFactory();
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IModbusFactory>();

            Assert.IsType<ModbusTcpClient>(factory.GetOrAdd("tcp", new ModbusTcpClientConfig()));
            Assert.IsType<ModbusRtuClient>(factory.GetOrAdd("rtu", new ModbusRtuClientConfig { PortName = "COM1" }));
        }

        [Fact]
        public void Di_AddModbusFactoryTwice_StillWorks()
        {
            var services = new ServiceCollection();
            services.AddModbusFactory();
            services.AddModbusFactory();
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IModbusFactory>();

            Assert.IsType<ModbusTcpClient>(factory.GetOrAdd("k", new ModbusTcpClientConfig()));
        }

        [Fact]
        public void Interface_HasNoConcreteConfigOverloads()
        {
            foreach (MethodInfo method in typeof(IModbusFactory).GetMethods())
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.True(
                        parameter.ParameterType != typeof(ModbusTcpClientConfig) && parameter.ParameterType != typeof(ModbusRtuClientConfig),
                        $"{method.Name} still exposes a concrete config overload ({parameter.ParameterType.Name})");
                }
            }
        }

        // ————————————————— 测试替身 —————————————————

        private static ModbusFactory CreateFactory(params IModbusClientCreator[] creators)
            => new(
                NullLogger<ModbusFactory>.Instance,
                creators,
                new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance));

        private sealed class FakeConfig : IModbusConfig
        {
            public int ReadTimeout { get; set; } = 2000;
            public int WriteTimeout { get; set; } = 2000;
            public int RetryCount { get; set; }
            public int RetryInterval { get; set; } = 100;
            public bool Reconnect { get; set; }
        }

        private sealed class DerivedTcpConfig : ModbusTcpClientConfig
        {
        }

        private sealed class TrackedModbus : IModbus
        {
            public int DisposeCount { get; private set; }

            public void Dispose() => DisposeCount++;

            public ModbusProtocolType ProtocolType => throw new NotSupportedException();
            public bool IsConnected => throw new NotSupportedException();
            public bool Connect() => throw new NotSupportedException();
            public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public void Disconnect() => throw new NotSupportedException();
            public ModbusResult<byte[]> Request(ModbusRequest tx) => throw new NotSupportedException();
            public Task<ModbusResult<byte[]>> RequestAsync(ModbusRequest tx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class FakeCreator : IModbusClientCreator
        {
            public FakeCreator(Type configType) => ConfigType = configType;

            public Type ConfigType { get; }

            public int NormalizeCalls { get; private set; }

            public int CreateCalls { get; private set; }

            public List<IModbus> Created { get; } = new();

            public void Normalize(IModbusConfig config) => NormalizeCalls++;

            public IModbus Create(IModbusConfig config)
            {
                CreateCalls++;
                var client = new TrackedModbus();
                Created.Add(client);
                return client;
            }

            public string Describe(IModbusConfig config) => "fake";
        }
    }
}