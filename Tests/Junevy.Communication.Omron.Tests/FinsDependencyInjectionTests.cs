using System.Text;
using Junevy.Communication.Omron.DependencyInjection;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS 的依赖注入（计划 Task 13 第 5 条，风格与 <c>S7DependencyInjectionTests</c> 一致）：<c>AddOmronFins</c> 注册创建器，
/// 经工厂以 <see cref="FinsClientConfig"/> 创建 TCP 客户端（FINS 只走 TCP，P4-D2）；传输缺失或节点号越界在创建时抛出；
/// appsettings 形状的 JSON 直接绑定到配置对象。创建客户端不打开连接或端口。
/// </summary>
public sealed class FinsDependencyInjectionTests
{
    [Fact(Timeout = 20000)]
    public void Factory_CreatesATcpClient()
    {
        using ServiceProvider provider = new ServiceCollection().AddOmronFins().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        IPlcClient client = factory.GetOrAdd("line", new FinsClientConfig
        {
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 9600 },
        });

        Assert.IsType<FinsClient>(client);
        Assert.Equal(1, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_ExistingName_ReturnsTheRegisteredClientAndIgnoresTheConfig()
    {
        using ServiceProvider provider = new ServiceCollection().AddOmronFins().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();
        IPlcClient first = factory.GetOrAdd("line", new FinsClientConfig { Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 9600 } });

        IPlcClient second = factory.GetOrAdd("line", new FinsClientConfig { Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 9601 } });

        Assert.Same(first, second);
    }

    [Fact(Timeout = 20000)]
    public void Creator_ConfigTypeIsTheFinsConfig()
    {
        Assert.Equal(typeof(FinsClientConfig), new FinsClientCreator().ConfigType);
    }

    [Fact(Timeout = 20000)]
    public void Creator_OtherConfigType_ThrowsArgumentException()
    {
        IPlcClientCreator creator = new FinsClientCreator();

        ArgumentException error = Assert.Throws<ArgumentException>(() => creator.Create("line", new OtherProtocolConfig(), NullLoggerFactory.Instance));

        Assert.Contains(nameof(FinsClientConfig), error.Message);
    }

    [Fact(Timeout = 20000)]
    public void Factory_TransportMissing_ThrowsArgumentException()
    {
        using ServiceProvider provider = new ServiceCollection().AddOmronFins().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        ArgumentException error = Assert.Throws<ArgumentException>(() => factory.GetOrAdd("line", new FinsClientConfig { Tcp = null! }));

        Assert.Contains(nameof(FinsClientConfig.Tcp), error.Message);
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_BroadcastClientNode_ThrowsArgumentOutOfRange()
    {
        // 节点号只在快照阶段校验一次（P4-D18），越界时客户端不会被登记到工厂里。
        using ServiceProvider provider = new ServiceCollection().AddOmronFins().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        Assert.Throws<ArgumentOutOfRangeException>(() => factory.GetOrAdd("line", new FinsClientConfig
        {
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 9600 },
            Options = new FinsClientOptions { ClientNode = 255 },
        }));
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void AddOmronFins_CalledTwice_RegistersOneCreatorAndOneFactory()
    {
        var services = new ServiceCollection();

        services.AddOmronFins().AddOmronFins();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPlcClientCreator));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PlcClientFactory));
    }

    [Fact(Timeout = 20000)]
    public void AddOmronFins_NullServices_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddOmronFins());
    }

    [Fact(Timeout = 20000)]
    public void AppsettingsShape_BindsToFinsClientConfigAndCreatesAClient()
    {
        // PlcDataFormat 只有构造函数入口、不能由配置绑定（P4-D19），因此 JSON 里只出现可以直接绑定的连接参数。
        const string json = """
            {
              "Omron": {
                "Tcp": { "Host": "192.168.0.30", "Port": 9600 },
                "Options": { "ClientNode": 11, "DestinationNetwork": 0, "DestinationUnit": 120 }
              }
            }
            """;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        FinsClientConfig bound = configuration.GetSection("Omron").Get<FinsClientConfig>()!;

        Assert.Equal("192.168.0.30", bound.Tcp.Host);
        Assert.Equal(9600, bound.Tcp.Port);
        Assert.Equal(2000, bound.Tcp.ConnectTimeout);
        Assert.Equal(11, bound.Options.ClientNode);
        Assert.Equal(0, bound.Options.DestinationNetwork);
        Assert.Equal(120, bound.Options.DestinationUnit);
        Assert.Null(bound.Options.DataFormat);

        using ServiceProvider provider = new ServiceCollection().AddOmronFins().BuildServiceProvider();
        IPlcClient client = provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line", bound);
        Assert.IsType<FinsClient>(client);
    }

    // 另一种协议形状的配置：工厂里只有 FINS 创建器时按精确类型匹配，创建器收到它时应报参数类型错误。
    private sealed class OtherProtocolConfig : IPlcClientConfig
    {
    }
}
