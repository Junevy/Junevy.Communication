using Junevy.Communication.OpcUa.DependencyInjection;
using Junevy.Communication.OpcUa.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 工厂与依赖注入（计划第 12 节、P61 Task 6）：同名同实例、不同名互不影响、释放、
/// <c>AddOpcUaClients()</c> 可重复调用、appsettings 形状的 JSON 绑定、容器日志工厂透传。
/// </summary>
public sealed class OpcUaClientFactoryTests
{
    private const string AppsettingsJson = """
        {
          "EndpointUrl": "opc.tcp://127.0.0.1:4840/UA/",
          "ApplicationName": "Junevy.Communication.OpcUa",
          "ApplicationUri": "urn:plc-1:Junevy.Communication.OpcUa",
          "PkiRoot": "C:\\ProgramData\\Junevy\\OpcUa\\pki",
          "ConnectTimeout": 15000,
          "OperationTimeout": 8000,
          "SessionTimeout": 45000,
          "KeepAliveInterval": 2500,
          "ReconnectInterval": 1500,
          "MaxReconnectInterval": 30000
        }
        """;

    private static OpcUaClientOptions OptionsFor(string name) => new OpcUaClientOptions
    {
        EndpointUrl = $"opc.tcp://127.0.0.1:4840/{name}/",
        PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
    };

    [Fact]
    public void GetOrAdd_ReturnsTheSameInstanceForTheSameName()
    {
        using var factory = new OpcUaClientFactory();

        OpcUaClient first = factory.GetOrAdd("line1", OptionsFor("line1"));
        OpcUaClient second = factory.GetOrAdd("line1", OptionsFor("other"));

        Assert.Same(first, second);
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public void GetOrAdd_KeepsDifferentNamesApart()
    {
        using var factory = new OpcUaClientFactory();

        OpcUaClient first = factory.GetOrAdd("line1", OptionsFor("a"));
        OpcUaClient second = factory.GetOrAdd("line2", OptionsFor("b"));

        // 每个名字保留自己的端点：Create 取的是创建时的快照。
        Assert.NotSame(first, second);
        Assert.Contains("/a/", first.ToString(), StringComparison.Ordinal);
        Assert.Contains("/b/", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public void GetOrAdd_NullOptions_Throws()
    {
        using var factory = new OpcUaClientFactory();

        Assert.Throws<ArgumentNullException>("options", () => factory.GetOrAdd("line1", null!));
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public void GetOrAdd_InvalidOptions_ThrowsWithThePropertyName()
    {
        using var factory = new OpcUaClientFactory();
        OpcUaClientOptions options = OptionsFor("a");
        options.MaxReconnectInterval = 500;

var error = Assert.Throws<ArgumentException>(() => factory.GetOrAdd("line1", options));

Assert.Equal("options.MaxReconnectInterval", error.ParamName);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task Dispose_ReleasesTheClients()
    {
        var factory = new OpcUaClientFactory();
        OpcUaClient client = factory.GetOrAdd("line1", OptionsFor("a"));

        factory.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheClients()
    {
        var factory = new OpcUaClientFactory();
        OpcUaClient client = factory.GetOrAdd("line1", OptionsFor("a"));

        await factory.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public void GetOrAdd_AfterDispose_Throws()
    {
        var factory = new OpcUaClientFactory();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("line1", OptionsFor("a")));
    }

    [Fact]
    public void GetOrAdd_TakesASnapshotOfTheOptions()
    {
        using var factory = new OpcUaClientFactory();
        OpcUaClientOptions options = OptionsFor("snapshot");
        OpcUaClient client = factory.GetOrAdd("line1", options);

        // 创建之后再改选项对象，不应影响已创建的客户端（P6-C9 的快照）。
        options.EndpointUrl = "opc.tcp://127.0.0.1:1/changed";

        Assert.Contains("snapshot", client.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_BindFromAnAppsettingsShapedJson()
    {
        // 走 appsettings 真正用的那条路：IConfiguration.Bind。
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AppsettingsJson)))
            .Build();

        var options = new OpcUaClientOptions();
        configuration.Bind(options);

        Assert.Equal("opc.tcp://127.0.0.1:4840/UA/", options.EndpointUrl);
        Assert.Equal("Junevy.Communication.OpcUa", options.ApplicationName);
        Assert.Equal("urn:plc-1:Junevy.Communication.OpcUa", options.ApplicationUri);
        Assert.Equal(@"C:\ProgramData\Junevy\OpcUa\pki", options.PkiRoot);
        Assert.Equal(15000, options.ConnectTimeout);
        Assert.Equal(8000, options.OperationTimeout);
        Assert.Equal(45000, options.SessionTimeout);
        Assert.Equal(2500, options.KeepAliveInterval);
        Assert.Equal(1500, options.ReconnectInterval);
        Assert.Equal(30000, options.MaxReconnectInterval);

        // 绑定出来的选项必须能通过校验，否则 appsettings 配错了要等到运行时才发现。
        OpcUaClient.Create(options);
    }
}

/// <summary>依赖注入注册。</summary>
public sealed class OpcUaDependencyInjectionTests
{
    private static OpcUaClientOptions OptionsFor(string path) => new OpcUaClientOptions
    {
        EndpointUrl = $"opc.tcp://127.0.0.1:4840/{path}/",
        PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
    };

    [Fact]
    public void AddOpcUaClients_RegistersASingletonFactory()
    {
        var services = new ServiceCollection();

        services.AddOpcUaClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<OpcUaClientFactory>(), provider.GetRequiredService<OpcUaClientFactory>());
    }

    [Fact]
    public void AddOpcUaClients_CanBeCalledTwice()
    {
        var services = new ServiceCollection();

        services.AddOpcUaClients();
        services.AddOpcUaClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<OpcUaClientFactory>());
        Assert.Single(services, service => service.ServiceType == typeof(OpcUaClientFactory));
    }

    [Fact]
    public void AddOpcUaClients_NullServices_Throws()
        => Assert.Throws<ArgumentNullException>("services", () => ((IServiceCollection)null!).AddOpcUaClients());

    [Fact]
    public void AddOpcUaClients_ResolvesAWorkingClient()
    {
        var services = new ServiceCollection();
        services.AddOpcUaClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        OpcUaClientFactory factory = provider.GetRequiredService<OpcUaClientFactory>();
        OpcUaClient client = factory.GetOrAdd("line1", OptionsFor("di-1"));

        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
        Assert.Same(client, factory.GetOrAdd("line1", OptionsFor("di-1")));
    }

    [Fact]
    public async Task AddOpcUaClients_PassesTheContainerLoggerFactoryThrough()
    {
        var logger = new TestLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logger);
        services.AddOpcUaClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        // 指向一个没人监听的端口：连接一定失败，而失败会记一条告警。
        OpcUaClientOptions options = OptionsFor("nothing-listens");
        options.ConnectTimeout = 3000;
        OpcUaClient client = provider.GetRequiredService<OpcUaClientFactory>().GetOrAdd("line1", options);

        Assert.False((await client.ConnectAsync()).IsSuccess);

        // 容器里的日志工厂被交给了客户端**和 SDK**（P62-D13），因此 SDK 自己的日志也能到记录器。
        // SDK 的告警是异步记录的，ConnectAsync 返回时可能还没写到：等它出现，而不是立即断言（并行负载下曾偶发失败）。
        await WaitUntilAsync(() => logger.Entries.Any(entry =>
            entry.CategoryName.StartsWith("Opc.Ua.", StringComparison.Ordinal) && entry.Level >= LogLevel.Warning));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("No Opc.Ua.* warning reached the container's logger factory in time.");
    }
}