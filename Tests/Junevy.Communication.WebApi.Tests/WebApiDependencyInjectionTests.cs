using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.DependencyInjection;
using Junevy.Communication.WebApi.Factory;
using Junevy.Communication.WebApi.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 构建器与依赖注入：<c>WebApiClientFactoryBuilder</c> 与 <c>AddWebApiClients</c>（风格参照 <c>PlcDependencyInjectionTests</c>）。
/// </summary>
public sealed class WebApiDependencyInjectionTests
{
    [Fact]
    public void Builder_BuildsAnIndependentFactoryEachTime()
    {
        WebApiClientFactoryBuilder builder = WebApiClientFactoryBuilder.Create();

        using WebApiClientFactory first = builder.Build();
        using WebApiClientFactory second = builder.Build();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Builder_FactoryWorks_AndUsesTheConfiguredLoggerFactory()
    {
        var logger = new TestLogger();
        using WebApiClientFactory factory = WebApiClientFactoryBuilder.Create().WithLoggerFactory(logger).Build();

        factory.GetOrAdd("report", new Rest.WebApiClientConfig { BaseUrl = "http://mes.local/api" });

        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public void Builder_NullLoggerFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WebApiClientFactoryBuilder.Create().WithLoggerFactory(null!));
    }

    [Fact]
    public void AddWebApiClients_RegistersOneFactorySingleton()
    {
        var services = new ServiceCollection();
        services.AddWebApiClients();

        using ServiceProvider provider = services.BuildServiceProvider();
        WebApiClientFactory factory = provider.GetRequiredService<WebApiClientFactory>();

        Assert.Same(factory, provider.GetRequiredService<WebApiClientFactory>());
        Assert.IsAssignableFrom<IWebApiEndpoint>(factory.GetOrAdd("trace", new Soap.SoapClientConfig { Url = "http://mes.local/service.asmx" }));
    }

    [Fact]
    public void AddWebApiClients_CalledTwice_RegistersTheFactoryOnce()
    {
        var services = new ServiceCollection();

        services.AddWebApiClients().AddWebApiClients();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(WebApiClientFactory));
    }

    [Fact]
    public async Task AddWebApiClients_UsesTheContainersLoggerFactoryWhenThereIsOne()
    {
        var logger = new TestLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logger);
        services.AddWebApiClients();
        using ServiceProvider provider = services.BuildServiceProvider();
        var handler = new StubHttpHandler().EnqueueException(new System.Net.Http.HttpRequestException("reset"));
        var components = new WebApiComponents { HttpHandler = handler };

        await provider.GetRequiredService<WebApiClientFactory>()
                      .GetOrAdd("report", new Rest.WebApiClientConfig { BaseUrl = "http://mes.local/api" }, components)
                      .GetAsync<string>("ping");

        Assert.Contains(logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("report"));
    }

    [Fact]
    public void AddWebApiClients_ReturnsTheSameCollection_AndRejectsNull()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddWebApiClients());
        Assert.Throws<ArgumentNullException>(() => WebApiServiceCollectionExtensions.AddWebApiClients(null!));
    }

    [Fact]
    public void DisposingTheProvider_DisposesTheFactoryAndItsClients()
    {
        var services = new ServiceCollection();
        services.AddWebApiClients();
        ServiceProvider provider = services.BuildServiceProvider();
        WebApiClientFactory factory = provider.GetRequiredService<WebApiClientFactory>();
        factory.GetOrAdd("report", new Rest.WebApiClientConfig { BaseUrl = "http://mes.local/api" });

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("report", new Rest.WebApiClientConfig { BaseUrl = "http://mes.local/api" }));
    }
}
