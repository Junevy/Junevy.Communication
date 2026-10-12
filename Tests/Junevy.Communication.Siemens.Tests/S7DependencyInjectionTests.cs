using System.Text;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens.DependencyInjection;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 的依赖注入（计划 Task 10 第 3 条，风格与 McDependencyInjectionTests 一致）：<c>AddSiemensS7</c> 注册创建器，
/// 经工厂以 <see cref="S7ClientConfig"/> 创建 TCP 客户端（S7 只走 TCP，P4-D2）；传输缺失或连接参数越界在创建时抛出；
/// appsettings 形状的 JSON 直接绑定到配置对象。创建客户端不打开连接或端口。
/// </summary>
public sealed class S7DependencyInjectionTests
{
    [Fact(Timeout = 20000)]
    public void Factory_CreatesATcpClient()
    {
        using ServiceProvider provider = new ServiceCollection().AddSiemensS7().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        IPlcClient client = factory.GetOrAdd("line", new S7ClientConfig
        {
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 102 },
        });

        Assert.IsType<S7Client>(client);
        Assert.Equal(1, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_ExistingName_ReturnsTheRegisteredClientAndIgnoresTheConfig()
    {
        using ServiceProvider provider = new ServiceCollection().AddSiemensS7().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();
        var first = factory.GetOrAdd("line", new S7ClientConfig { Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 102 } });

        IPlcClient second = factory.GetOrAdd("line", new S7ClientConfig { Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 1020 } });

        Assert.Same(first, second);
    }

    [Fact(Timeout = 20000)]
    public void Creator_ConfigTypeIsTheS7Config()
    {
        Assert.Equal(typeof(S7ClientConfig), new S7ClientCreator().ConfigType);
    }

    [Fact(Timeout = 20000)]
    public void Creator_OtherConfigType_ThrowsArgumentException()
    {
        IPlcClientCreator creator = new S7ClientCreator();

        ArgumentException error = Assert.Throws<ArgumentException>(() => creator.Create("line", new OtherProtocolConfig(), NullLoggerFactory.Instance));

        Assert.Contains(nameof(S7ClientConfig), error.Message);
    }

    [Fact(Timeout = 20000)]
    public void Factory_TransportMissing_ThrowsArgumentException()
    {
        using ServiceProvider provider = new ServiceCollection().AddSiemensS7().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        ArgumentException error = Assert.Throws<ArgumentException>(() => factory.GetOrAdd("line", new S7ClientConfig { Tcp = null! }));

        Assert.Contains(nameof(S7ClientConfig.Tcp), error.Message);
        Assert.Equal(0, factory.Count);
    }

    [Theory(Timeout = 20000)]
    [InlineData(8, 1, S7ConnectionType.PG)]
    [InlineData(0, 32, S7ConnectionType.PG)]
    [InlineData(0, 1, (S7ConnectionType)99)]
    public void Factory_ConnectionParamsBeyondTheS7Ranges_ThrowArgumentOutOfRange(int rack, int slot, S7ConnectionType connectionType)
    {
        // 连接参数的范围只在快照阶段校验一次（P4-D19），越界时客户端不会被登记到工厂里。
        using ServiceProvider provider = new ServiceCollection().AddSiemensS7().BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        Assert.Throws<ArgumentOutOfRangeException>(() => factory.GetOrAdd("line", new S7ClientConfig
        {
            Tcp = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 102 },
            Options = new S7ClientOptions { Rack = rack, Slot = slot, ConnectionType = connectionType },
        }));
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void AddSiemensS7_CalledTwice_RegistersOneCreatorAndOneFactory()
    {
        var services = new ServiceCollection();

        services.AddSiemensS7().AddSiemensS7();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPlcClientCreator));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PlcClientFactory));
    }

    [Fact(Timeout = 20000)]
    public void AddSiemensS7_NullServices_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddSiemensS7());
    }

    [Fact(Timeout = 20000)]
    public void AppsettingsShape_BindsToS7ClientConfigAndCreatesAClient()
    {
        // PlcDataFormat 只有构造函数入口、不能由配置绑定（P4-D19），因此 JSON 里只出现可以直接绑定的连接参数。
        const string json = """
            {
              "S7": {
                "Tcp": { "Host": "192.168.0.20", "Port": 102 },
                "Options": { "Rack": 0, "Slot": 1, "ConnectionType": "Basic" }
              }
            }
            """;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        S7ClientConfig bound = configuration.GetSection("S7").Get<S7ClientConfig>()!;

        Assert.Equal("192.168.0.20", bound.Tcp.Host);
        Assert.Equal(102, bound.Tcp.Port);
        Assert.Equal(2000, bound.Tcp.ConnectTimeout);
        Assert.Equal(0, bound.Options.Rack);
        Assert.Equal(1, bound.Options.Slot);
        Assert.Equal(S7ConnectionType.Basic, bound.Options.ConnectionType);

        using ServiceProvider provider = new ServiceCollection().AddSiemensS7().BuildServiceProvider();
        IPlcClient client = provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line", bound);
        Assert.IsType<S7Client>(client);
    }

    // 另一种协议形状的配置：工厂里只有 S7 创建器时按精确类型匹配，创建器收到它时应报参数类型错误。
    private sealed class OtherProtocolConfig : IPlcClientConfig
    {
    }
}
