using System.Net;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 服务器工厂（P32-D14）：同名同实例、不同名、别名、配置快照、空参数与非法配置、释放与重复释放、释放后的调用，以及工厂创建的服务器真正可用。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class WebApiServerFactoryTests
{
    [Fact]
    public void GetOrAdd_TheSameName_ReturnsTheSameInstance()
    {
        using var factory = new WebApiServerFactory();

        IWebApiServer first = factory.GetOrAdd("mes", LoopbackConfig());
        IWebApiServer second = factory.GetOrAdd("mes", LoopbackConfig());

        Assert.Same(first, second);
        Assert.Equal("mes", first.Name);
    }

    [Fact]
    public void GetOrAdd_DifferentNames_CreateDifferentServers()
    {
        using var factory = new WebApiServerFactory();

        IWebApiServer first = factory.GetOrAdd("mes-1", LoopbackConfig());
        IWebApiServer second = factory.GetOrAdd("mes-2", LoopbackConfig());

        Assert.NotSame(first, second);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public void GetOrAdd_NullConfig_IsArgumentNullException()
    {
        using var factory = new WebApiServerFactory();

        Assert.Throws<ArgumentNullException>(() => factory.GetOrAdd("mes", null!));
    }

    [Fact]
    public void GetOrAdd_InvalidConfig_IsArgumentException_AndNothingIsRegistered()
    {
        using var factory = new WebApiServerFactory();
        var config = new WebApiServerConfig { Urls = Array.Empty<string>() };

        Assert.Throws<ArgumentException>(() => factory.GetOrAdd("mes", config));
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public void GetOrAdd_EmptyName_IsArgumentException()
    {
        using var factory = new WebApiServerFactory();

        Assert.ThrowsAny<ArgumentException>(() => factory.GetOrAdd(string.Empty, LoopbackConfig()));
    }

    [Fact]
    public void GetOrAdd_TakesASnapshotOfTheConfig()
    {
        using var factory = new WebApiServerFactory();
        var config = LoopbackConfig();

        IWebApiServer server = factory.GetOrAdd("mes", config);
        config.Urls = new[] { "http://127.0.0.1:9999" };

        Assert.Equal("http://127.0.0.1:0", Assert.Single(server.Urls));
    }

    [Fact]
    public void Alias_SharesTheServerWithItsName()
    {
        using var factory = new WebApiServerFactory();
        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());

        Assert.True(factory.RegisterAlias("mes-alias", "mes"));

        Assert.True(factory.TryGet("mes-alias", out IWebApiServer? viaAlias));
        Assert.Same(server, viaAlias);
    }

    [Fact]
    public void TryRemove_DisposesTheServer()
    {
        using var factory = new WebApiServerFactory();
        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());

        Assert.True(factory.TryRemove("mes"));

        Assert.Throws<ObjectDisposedException>(() => { _ = server.StartAsync(); });
    }

    [Fact]
    public async Task DisposingTheFactory_DisposesItsServers_AndCanBeRepeated()
    {
        var factory = new WebApiServerFactory();
        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());

        await factory.DisposeAsync();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = server.StartAsync(); });
    }

    [Fact]
    public void AfterDispose_GetOrAddIsObjectDisposed()
    {
        var factory = new WebApiServerFactory();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("mes", LoopbackConfig()));
    }

    [Fact]
    public async Task ServersFromTheFactory_ServeRequests()
    {
        using var factory = new WebApiServerFactory(new TestLogger());
        IWebApiServer server = factory.GetOrAdd("mes", LoopbackConfig());
        server.MapGet<Order>("/orders/{id:int}", (context, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = int.Parse(context.RouteValues["id"]) })));
        CommResult started = await server.StartAsync();
        Assert.True(started.IsSuccess, started.ErrorMessage);

        using var http = new HttpClient();
        HttpResponseMessage response = await http.GetAsync(server.Urls[0] + "/orders/5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await server.StopAsync();
    }

    private static WebApiServerConfig LoopbackConfig() => new WebApiServerConfig { Urls = new[] { "http://127.0.0.1:0" } };
}
