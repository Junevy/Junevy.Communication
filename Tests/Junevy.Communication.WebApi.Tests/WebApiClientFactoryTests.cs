using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Factory;
using Junevy.Communication.WebApi.Rest;
using Junevy.Communication.WebApi.Soap;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiClientFactory"/> 的测试（计划 Task 11）：风格参照 <c>PlcClientFactoryTests</c>。名称、别名、移除即释放与工厂释放后的行为由
/// <c>NamedFactory</c> 提供，这里只验证它们对 WebApi 客户端成立，以及 REST / SOAP 类型不符时的错误。
/// </summary>
public sealed class WebApiClientFactoryTests
{
    private static WebApiClientConfig RestConfig() => new WebApiClientConfig { BaseUrl = "http://mes.local/api" };

    private static SoapClientConfig SoapConfig() => new SoapClientConfig { Url = "http://mes.local/service.asmx" };

    [Fact]
    public void GetOrAdd_Rest_CreatesTheClientOnceAndRegistersIt()
    {
        using var factory = new WebApiClientFactory();

        IWebApiClient client = factory.GetOrAdd("report", RestConfig());

        Assert.IsType<WebApiClient>(client);
        Assert.Equal("report", client.Name);
        Assert.Same(client, factory.GetOrAdd("report", RestConfig()));
        Assert.Equal(new[] { "report" }, factory.Keys);
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public void GetOrAdd_Soap_CreatesTheClientOnceAndRegistersIt()
    {
        using var factory = new WebApiClientFactory();

        ISoapClient client = factory.GetOrAdd("trace", SoapConfig());

        Assert.IsType<SoapClient>(client);
        Assert.Equal("trace", client.Name);
        Assert.Same(client, factory.GetOrAdd("trace", SoapConfig()));
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public void GetOrAdd_ExistingName_IgnoresTheNewConfig()
    {
        using var factory = new WebApiClientFactory();
        IWebApiClient first = factory.GetOrAdd("report", RestConfig());

        IWebApiClient second = factory.GetOrAdd("report", new WebApiClientConfig { BaseUrl = "http://other.local/" });

        Assert.Same(first, second);
        Assert.Equal("http://mes.local/api", second.Config.BaseUrl);
    }

    [Fact]
    public void GetOrAdd_RestNameAlreadyRegisteredAsSoap_ThrowsNamingTheExistingType()
    {
        using var factory = new WebApiClientFactory();
        factory.GetOrAdd("shared", SoapConfig());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.GetOrAdd("shared", RestConfig()));

        Assert.Contains("SoapClient", exception.Message);
        Assert.Contains("shared", exception.Message);
        Assert.Contains(nameof(IWebApiClient), exception.Message);
    }

    [Fact]
    public void GetOrAdd_SoapNameAlreadyRegisteredAsRest_ThrowsNamingTheExistingType()
    {
        using var factory = new WebApiClientFactory();
        factory.GetOrAdd("shared", RestConfig());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.GetOrAdd("shared", SoapConfig()));

        Assert.Contains("WebApiClient", exception.Message);
        Assert.Contains(nameof(ISoapClient), exception.Message);
    }

    [Fact]
    public void TheMismatchDoesNotDisturbTheRegisteredClient()
    {
        using var factory = new WebApiClientFactory();
        IWebApiClient client = factory.GetOrAdd("shared", RestConfig());

        Assert.Throws<InvalidOperationException>(() => factory.GetOrAdd("shared", SoapConfig()));

        Assert.Same(client, factory.GetOrAdd("shared", RestConfig()));
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public void Alias_ResolvesToTheTargetClient_AndMismatchesAreDetectedThroughIt()
    {
        using var factory = new WebApiClientFactory();
        IWebApiClient client = factory.GetOrAdd("report", RestConfig());
        Assert.True(factory.RegisterAlias("alias", "report"));

        Assert.Same(client, factory.GetOrAdd("alias", RestConfig()));
        Assert.Throws<InvalidOperationException>(() => factory.GetOrAdd("alias", SoapConfig()));
        Assert.Same(client, factory.GetRequired<IWebApiClient>("alias"));
    }

    [Fact]
    public void GetRequired_WithTheWrongKind_ThrowsInvalidCastException()
    {
        using var factory = new WebApiClientFactory();
        factory.GetOrAdd("report", RestConfig());

        Assert.Throws<InvalidCastException>(() => factory.GetRequired<ISoapClient>("report"));
        Assert.True(factory.TryGet("report", out IWebApiEndpoint? endpoint));
        Assert.IsType<WebApiClient>(endpoint);
    }

    [Fact]
    public void GetOrAdd_NullConfig_ThrowsArgumentNullException()
    {
        using var factory = new WebApiClientFactory();

        Assert.Throws<ArgumentNullException>(() => factory.GetOrAdd("report", (WebApiClientConfig)null!));
        Assert.Throws<ArgumentNullException>(() => factory.GetOrAdd("trace", (SoapClientConfig)null!));
        Assert.Equal(0, factory.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetOrAdd_EmptyName_ThrowsArgumentException(string? name)
    {
        using var factory = new WebApiClientFactory();

        Assert.Throws<ArgumentException>(() => factory.GetOrAdd(name!, RestConfig()));
        Assert.Throws<ArgumentException>(() => factory.GetOrAdd(name!, SoapConfig()));
    }

    [Fact]
    public void GetOrAdd_InvalidConfig_ThrowsAndRegistersNothing()
    {
        using var factory = new WebApiClientFactory();

        Assert.Throws<ArgumentException>(() => factory.GetOrAdd("report", new WebApiClientConfig { BaseUrl = "not a url" }));
        Assert.Throws<ArgumentException>(() => factory.GetOrAdd("trace", new SoapClientConfig { Url = "" }));

        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task GetOrAdd_PassesTheComponentsToTheClient()
    {
        using var factory = new WebApiClientFactory();
        var handler = new StubHttpHandler().Enqueue(200, "ok", "text/plain; charset=utf-8");
        var seen = new List<string>();
        var components = new WebApiComponents { HttpHandler = handler };
        components.Interceptors.Add(ScriptedInterceptor.Sending("spy", context => seen.Add(context.Method)));
        IWebApiClient client = factory.GetOrAdd("report", RestConfig(), components);

        WebApiResult<string> result = await client.GetAsync<string>("ping");

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "GET" }, seen);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetOrAdd_GivesTheClientsTheFactorysLoggerFactory()
    {
        var logger = new TestLogger();
        using var factory = new WebApiClientFactory(logger);
        var handler = new StubHttpHandler().EnqueueException(new HttpRequestException("reset"));
        IWebApiClient client = factory.GetOrAdd("report", RestConfig(), new WebApiComponents { HttpHandler = handler });

        await client.GetAsync<string>("ping");

        Assert.Contains(logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("report"));
    }

    [Fact]
    public async Task TryRemove_DisposesTheClient()
    {
        using var factory = new WebApiClientFactory();
        var handler = new StubHttpHandler().Enqueue(200);
        IWebApiClient client = factory.GetOrAdd("report", RestConfig(), new WebApiComponents { HttpHandler = handler });

        Assert.True(factory.TryRemove("report"));
        WebApiResult<string> result = await client.GetAsync<string>("ping");

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task TryRemove_StopsTheHealthCheck()
    {
        var probed = new SemaphoreSlim(0);
        var handler = new StubHttpHandler();
        for (int i = 0; i < 50; i++)
        {
            handler.EnqueueAsync((request, token) =>
            {
                probed.Release();
                return Task.FromResult(new HttpResponseMessage());
            });
        }

        using var factory = new WebApiClientFactory();
        factory.GetOrAdd("report", new WebApiClientConfig { BaseUrl = "http://mes.local/api", HealthCheckPath = "health", HealthCheckInterval = 20 },
                         new WebApiComponents { HttpHandler = handler });
        Assert.True(await probed.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(factory.TryRemove("report"));

        int afterRemove = handler.Requests.Count;
        await Task.Delay(150);
        Assert.Equal(afterRemove, handler.Requests.Count);
    }

    [Fact]
    public async Task Dispose_DisposesEveryClient_AndLaterUseThrows()
    {
        var factory = new WebApiClientFactory();
        IWebApiClient rest = factory.GetOrAdd("report", RestConfig());
        ISoapClient soap = factory.GetOrAdd("trace", SoapConfig());

        factory.Dispose();

        Assert.Equal(CommErrorKind.NotConnected, (await rest.GetAsync<string>("ping")).ErrorKind);
        Assert.Equal(CommErrorKind.NotConnected, (await soap.InvokeRawAsync("a", new System.Xml.Linq.XElement("A"))).ErrorKind);
        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("report", RestConfig()));
        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("trace", SoapConfig()));
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryClient()
    {
        var factory = new WebApiClientFactory();
        IWebApiClient rest = factory.GetOrAdd("report", RestConfig());

        await factory.DisposeAsync();

        Assert.Equal(CommErrorKind.NotConnected, (await rest.GetAsync<string>("ping")).ErrorKind);
    }

    [Fact]
    public void FactoryIsAnIWebApiEndpointRegistry()
    {
        using var factory = new WebApiClientFactory();
        factory.GetOrAdd("report", RestConfig());
        factory.GetOrAdd("trace", SoapConfig());

        Assert.Equal(new[] { "report", "trace" }, factory.Keys.OrderBy(key => key));
        Assert.IsAssignableFrom<IWebApiEndpoint>(factory.GetRequired<IWebApiEndpoint>("trace"));
    }
}
