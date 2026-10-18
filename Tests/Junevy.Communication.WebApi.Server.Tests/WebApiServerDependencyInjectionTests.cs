using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.DependencyInjection;
using Junevy.Communication.WebApi.Factory;
using Junevy.Communication.WebApi.Server.DependencyInjection;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 依赖注入（P32-D14）：<c>AddWebApiServers</c> 的单例与幂等，与 <c>AddWebApiClients</c> 互不影响，容器的日志工厂透传，
/// 容器释放时释放服务器，以及 appsettings 形状的 JSON 经绑定路径得到的配置可以直接启动服务器。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class WebApiServerDependencyInjectionTests
{
    [Fact]
    public void AddWebApiServers_RegistersOneFactorySingleton()
    {
        var services = new ServiceCollection().AddWebApiServers();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<WebApiServerFactory>(), provider.GetRequiredService<WebApiServerFactory>());
    }

    [Fact]
    public void AddWebApiServers_CalledTwice_RegistersTheFactoryOnce()
    {
        var services = new ServiceCollection().AddWebApiServers().AddWebApiServers();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(WebApiServerFactory));
    }

    [Fact]
    public void AddWebApiServers_AndAddWebApiClients_CoexistWithoutInterference()
    {
        var services = new ServiceCollection().AddWebApiClients().AddWebApiServers();

        using ServiceProvider provider = services.BuildServiceProvider();
        using WebApiClientFactory clients = provider.GetRequiredService<WebApiClientFactory>();
        WebApiServerFactory servers = provider.GetRequiredService<WebApiServerFactory>();

        Assert.NotNull(clients);
        Assert.NotNull(servers);
        Assert.Equal(0, servers.Count);
        Assert.Equal(0, clients.Count);
    }

    [Fact]
    public void AddWebApiServers_ReturnsTheSameCollection_AndRejectsNull()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddWebApiServers());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddWebApiServers());
    }

    [Fact]
    public async Task ContainerLoggerFactory_IsUsedByTheServers()
    {
        var logger = new TestLogger();
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logger).AddWebApiServers();
        using ServiceProvider provider = services.BuildServiceProvider();
        WebApiServerFactory factory = provider.GetRequiredService<WebApiServerFactory>();
        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        await server.StartAsync();
        await server.StopAsync();

        Assert.Contains(logger.Entries, entry => entry.CategoryName == "Junevy.Communication.WebApi.Server" && entry.Message.Contains("changed state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposingTheProvider_DisposesTheFactoryAndItsServers()
    {
        var services = new ServiceCollection().AddWebApiServers();
        ServiceProvider provider = services.BuildServiceProvider();
        IWebApiServer server = provider.GetRequiredService<WebApiServerFactory>().GetOrAdd("mes", LoopbackConfig());

        await provider.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => { _ = server.StartAsync(); });
    }

    [Fact]
    public async Task ConfigurationJson_BindsToAConfig_AndTheServerStarts()
    {
        const string json = """
            {
              "Server": {
                "Urls": [ "http://127.0.0.1:0" ],
                "MaxConcurrentRequests": 4,
                "RequestTimeout": 5000,
                "Envelope": {
                  "Enabled": true,
                  "CodePath": "result.code",
                  "MessagePath": "result.msg",
                  "DataPath": "result.data",
                  "SuccessCodes": [ "0" ]
                }
              }
            }
            """;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        WebApiServerConfig config = configuration.GetSection("Server").Get<WebApiServerConfig>()!;
        var services = new ServiceCollection().AddWebApiServers();
        using ServiceProvider provider = services.BuildServiceProvider();
        IWebApiServer server = provider.GetRequiredService<WebApiServerFactory>().GetOrAdd("mes", config);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = 3 })));

        CommResult started = await server.StartAsync();

        Assert.True(started.IsSuccess, started.ErrorMessage);
        Assert.Equal(4, config.MaxConcurrentRequests);
        Assert.True(config.Envelope.Enabled);
        Assert.Equal("result.code", config.Envelope.CodePath);
        await server.StopAsync();
    }

    private static WebApiServerConfig LoopbackConfig() => new WebApiServerConfig { Urls = new[] { "http://127.0.0.1:0" } };
}
