using System.Text;
using Junevy.Communication.Melsec.DependencyInjection;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// MELSEC 的依赖注入（计划 Task 13）：<c>AddMelsec</c> 注册创建器，经工厂创建三种传输的客户端；
/// 配置缺失抛出 ArgumentException；appsettings 形状的 JSON 绑定到 <see cref="McClientConfig"/>。
/// 创建客户端不打开连接或端口。
/// </summary>
public sealed class McDependencyInjectionTests
{
    [Fact(Timeout = 20000)]
    public void Factory_CreatesAClientForEachTransport()
    {
        using ServiceProvider provider = new ServiceCollection().AddMelsec().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        IPlcClient tcp = factory.GetOrAdd("tcp", new McClientConfig
        {
            Transport = McTransport.Tcp,
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5000 },
        });
        IPlcClient udp = factory.GetOrAdd("udp", new McClientConfig
        {
            Transport = McTransport.Udp,
            Udp = new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = 5001 },
        });
        IPlcClient serial = factory.GetOrAdd("serial", new McClientConfig
        {
            Transport = McTransport.Serial,
            Serial = new SerialChannelConfig(),
            Options = new McClientOptions { FrameType = McFrameType.Frame4C },
        });

        Assert.All(new[] { tcp, udp, serial }, client => Assert.IsType<McClient>(client));
        Assert.Equal(3, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_ExistingName_ReturnsTheRegisteredClientAndIgnoresTheConfig()
    {
        using ServiceProvider provider = new ServiceCollection().AddMelsec().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();
        var tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5000 };
        IPlcClient first = factory.GetOrAdd("line", new McClientConfig { Transport = McTransport.Tcp, Tcp = tcp });

        IPlcClient second = factory.GetOrAdd("line", new McClientConfig { Transport = McTransport.Udp, Udp = new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = 5001 } });

        Assert.Same(first, second);
    }

    [Theory(Timeout = 20000)]
    [InlineData(McTransport.Tcp, McFrameType.Frame3E)]
    [InlineData(McTransport.Udp, McFrameType.Frame3E)]
    [InlineData(McTransport.Serial, McFrameType.Frame4C)]
    public void Factory_TransportWithoutItsConfig_ThrowsArgumentException(McTransport transport, McFrameType frameType)
    {
        using ServiceProvider provider = new ServiceCollection().AddMelsec().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();
        var config = new McClientConfig { Transport = transport, Options = new McClientOptions { FrameType = frameType } };

        ArgumentException error = Assert.Throws<ArgumentException>(() => factory.GetOrAdd("line", config));

        Assert.Contains(transport.ToString(), error.Message);
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_SerialWithAThreeEFrame_ThrowsArgumentException()
    {
        using ServiceProvider provider = new ServiceCollection().AddMelsec().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        Assert.Throws<ArgumentException>(() => factory.GetOrAdd("serial", new McClientConfig
        {
            Transport = McTransport.Serial,
            Serial = new SerialChannelConfig(),
        }));
    }

    [Fact(Timeout = 20000)]
    public void AddMelsec_CalledTwice_RegistersOneCreatorAndOneFactory()
    {
        var services = new ServiceCollection();

        services.AddMelsec().AddMelsec();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPlcClientCreator));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PlcClientFactory));
    }

    [Fact(Timeout = 20000)]
    public void AddMelsec_NullServices_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddMelsec());
    }

    [Fact(Timeout = 20000)]
    public void AppsettingsShape_BindsToMcClientConfigAndCreatesAClient()
    {
        const string json = """
            {
              "Mc": {
                "Transport": "Tcp",
                "Tcp": { "Host": "192.168.0.10", "Port": 5000 },
                "Options": { "FrameType": "Frame4E", "Encoding": "Ascii", "SumCheck": false }
              }
            }
            """;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        McClientConfig bound = configuration.GetSection("Mc").Get<McClientConfig>()!;

        Assert.Equal(McTransport.Tcp, bound.Transport);
        Assert.Equal("192.168.0.10", bound.Tcp!.Host);
        Assert.Equal(5000, bound.Tcp.Port);
        Assert.Equal(McFrameType.Frame4E, bound.Options.FrameType);
        Assert.Equal(McEncoding.Ascii, bound.Options.Encoding);
        Assert.False(bound.Options.SumCheck);

        using ServiceProvider provider = new ServiceCollection().AddMelsec().BuildServiceProvider();
        IPlcClient client = provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line", bound);
        Assert.IsType<McClient>(client);
    }
}
