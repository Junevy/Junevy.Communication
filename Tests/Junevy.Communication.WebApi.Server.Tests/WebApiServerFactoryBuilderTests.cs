using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 无 DI 容器的服务器工厂构建器（P32-D14）：构建出的工厂可以创建服务器，日志工厂透传，空参数检查，以及每次构建都是独立的工厂。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class WebApiServerFactoryBuilderTests
{
    [Fact]
    public void Build_ReturnsAFactoryThatCreatesServers()
    {
        using WebApiServerFactory factory = WebApiServerFactoryBuilder.Create().Build();

        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());

        Assert.Equal("mes", server.Name);
        Assert.Same(server, factory.GetOrAdd("mes", LoopbackConfig()));
    }

    [Fact]
    public async Task Build_WithLoggerFactory_LogsThroughIt()
    {
        var logger = new TestLogger();
        using WebApiServerFactory factory = WebApiServerFactoryBuilder.Create().WithLoggerFactory(logger).Build();
        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        await server.StartAsync();
        await server.StopAsync();

        Assert.Contains(logger.Entries, entry => entry.CategoryName == "Junevy.Communication.WebApi.Server" && entry.Message.Contains("changed state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_WithoutLoggerFactory_CreatesAStartableServer()
    {
        using WebApiServerFactory factory = WebApiServerFactoryBuilder.Create().Build();
        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        CommResult started = await server.StartAsync();

        Assert.True(started.IsSuccess, started.ErrorMessage);
        await server.StopAsync();
    }

    [Fact]
    public void WithLoggerFactory_Null_IsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WebApiServerFactoryBuilder.Create().WithLoggerFactory(null!));
    }

    [Fact]
    public async Task Build_EachCallReturnsAnIndependentFactory()
    {
        WebApiServerFactoryBuilder builder = WebApiServerFactoryBuilder.Create();
        using WebApiServerFactory first = builder.Build();
        using WebApiServerFactory second = builder.Build();
        IWebApiServer firstServer = first.GetOrAdd("mes", LoopbackConfig());
        IWebApiServer secondServer = second.GetOrAdd("mes", LoopbackConfig());
        secondServer.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        Assert.NotSame(firstServer, secondServer);
        first.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = firstServer.StartAsync(); });
        Assert.True((await secondServer.StartAsync()).IsSuccess);
        await secondServer.StopAsync();
    }

    private static WebApiServerConfig LoopbackConfig() => new WebApiServerConfig { Urls = new[] { "http://127.0.0.1:0" } };
}
