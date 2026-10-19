using System.Text;
using Junevy.Communication.Melsec;
using Junevy.Communication.Melsec.DependencyInjection;
using Junevy.Communication.Modbus.DependencyInjection;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.Modbus.Tests.DependencyInjection;

/// <summary>
/// Modbus 的依赖注入（P7-D14）：<c>AddModbus</c> 注册创建器，经工厂创建三种传输的客户端；配置缺失或类型不符抛 ArgumentException；
/// 重复注册幂等；与 <c>AddMelsec</c> 共存；appsettings 形状的 JSON 绑定到 <see cref="ModbusClientConfig"/>。创建客户端不打开连接或端口。
/// </summary>
public sealed class ModbusDependencyInjectionTests
{
    [Fact(Timeout = 20000)]
    public void Factory_CreatesAClientForEachTransport()
    {
        using ServiceProvider provider = new ServiceCollection().AddModbus().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        IPlcClient tcp = factory.GetOrAdd("tcp", new ModbusClientConfig
        {
            Transport = ModbusTransport.Tcp,
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 502 },
        });
        IPlcClient udp = factory.GetOrAdd("udp", new ModbusClientConfig
        {
            Transport = ModbusTransport.Udp,
            Udp = new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = 502 },
        });
        IPlcClient serial = factory.GetOrAdd("serial", new ModbusClientConfig
        {
            Transport = ModbusTransport.Serial,
            Serial = new SerialChannelConfig(),
        });

        Assert.All(new[] { tcp, udp, serial }, client => Assert.IsType<ModbusClient>(client));
        Assert.Equal(3, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_ExistingName_ReturnsTheRegisteredClientAndIgnoresTheConfig()
    {
        using ServiceProvider provider = new ServiceCollection().AddModbus().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        IPlcClient first = factory.GetOrAdd("line", new ModbusClientConfig
        {
            Transport = ModbusTransport.Tcp,
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 502 },
        });
        IPlcClient second = factory.GetOrAdd("line", new ModbusClientConfig
        {
            Transport = ModbusTransport.Udp,
            Udp = new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = 502 },
        });

        Assert.Same(first, second);
    }

    [Theory(Timeout = 20000)]
    [InlineData(ModbusTransport.Tcp)]
    [InlineData(ModbusTransport.Udp)]
    [InlineData(ModbusTransport.Serial)]
    public void Factory_TransportWithoutItsConfig_ThrowsArgumentException(ModbusTransport transport)
    {
        using ServiceProvider provider = new ServiceCollection().AddModbus().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        ArgumentException error = Assert.Throws<ArgumentException>(() => factory.GetOrAdd("line", new ModbusClientConfig { Transport = transport }));

        Assert.Contains(transport.ToString(), error.Message);
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Creator_ConfigOfAnotherType_ThrowsArgumentException()
    {
        var creator = new ModbusClientCreator();
        var wrong = new Junevy.Communication.Melsec.DependencyInjection.McClientConfig();

        ArgumentException error = Assert.Throws<ArgumentException>(() => creator.Create("line", wrong, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));

        Assert.Contains("ModbusClientConfig", error.Message);
    }

    [Fact(Timeout = 20000)]
    public void Factory_SerialWithMbap_ThrowsArgumentException()
    {
        using ServiceProvider provider = new ServiceCollection().AddModbus().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        Assert.Throws<ArgumentException>(() => factory.GetOrAdd("serial", new ModbusClientConfig
        {
            Transport = ModbusTransport.Serial,
            Serial = new SerialChannelConfig(),
            Options = new ModbusClientOptions { Framing = ModbusFraming.Mbap },
        }));
    }

    [Fact(Timeout = 20000)]
    public void AddModbus_CalledTwice_RegistersOneCreatorAndOneFactory()
    {
        var services = new ServiceCollection();

        services.AddModbus().AddModbus();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPlcClientCreator));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PlcClientFactory));
    }

    [Fact(Timeout = 20000)]
    public void AddModbus_AndAddMelsec_CoexistInOneFactory()
    {
        using ServiceProvider provider = new ServiceCollection().AddModbus().AddMelsec().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        IPlcClient modbus = factory.GetOrAdd("modbus", new ModbusClientConfig
        {
            Transport = ModbusTransport.Tcp,
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 502 },
        });
        IPlcClient melsec = factory.GetOrAdd("melsec", new McClientConfig
        {
            Transport = McTransport.Tcp,
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5000 },
        });

        Assert.IsType<ModbusClient>(modbus);
        Assert.IsType<McClient>(melsec);
    }

    [Fact(Timeout = 20000)]
    public void AddModbus_NullServices_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddModbus());
    }

    [Fact(Timeout = 20000)]
    public void Builder_WithTheModbusCreator_CreatesClients()
    {
        PlcClientFactory factory = PlcClientFactoryBuilder.Create().WithCreator(new ModbusClientCreator()).Build();

        IPlcClient client = factory.GetOrAdd("line", new ModbusClientConfig
        {
            Transport = ModbusTransport.Tcp,
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 502 },
        });

        Assert.IsType<ModbusClient>(client);
    }

    [Fact(Timeout = 20000)]
    public void AppsettingsShape_BindsToModbusClientConfigAndCreatesAClient()
    {
        const string json = """
            {
              "Modbus": {
                "Transport": "Tcp",
                "Tcp": { "Host": "192.168.0.10", "Port": 502 },
                "Options": { "Framing": "Rtu", "UnitId": 3, "UseSingleWriteFunctions": false }
              }
            }
            """;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        ModbusClientConfig bound = configuration.GetSection("Modbus").Get<ModbusClientConfig>()!;

        Assert.Equal(ModbusTransport.Tcp, bound.Transport);
        Assert.Equal("192.168.0.10", bound.Tcp!.Host);
        Assert.Equal(502, bound.Tcp.Port);
        Assert.Equal(ModbusFraming.Rtu, bound.Options.Framing);
        Assert.Equal((byte)3, bound.Options.UnitId);
        Assert.False(bound.Options.UseSingleWriteFunctions);

        using ServiceProvider provider = new ServiceCollection().AddModbus().BuildServiceProvider();
        IPlcClient client = provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line", bound);
        Assert.IsType<ModbusClient>(client);
    }
}
