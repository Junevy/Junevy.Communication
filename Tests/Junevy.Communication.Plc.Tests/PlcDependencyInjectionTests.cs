using Junevy.Communication.Plc.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 客户端构建器与依赖注入：<c>PlcClientFactoryBuilder</c> 与 <c>AddPlcClients</c>。
/// </summary>
public sealed class PlcDependencyInjectionTests
{
    [Fact(Timeout = 20000)]
    public void Builder_BuildsAFactoryWithTheRegisteredCreator()
    {
        var creator = new StubCreator();
        using PlcClientFactory factory = PlcClientFactoryBuilder.Create()
            .WithLoggerFactory(NullLoggerFactory.Instance)
            .WithCreator(creator)
            .Build();

        factory.GetOrAdd("line", new StubConfig());

        Assert.Equal(1, creator.CreateCount);
    }

    [Fact(Timeout = 20000)]
    public void Builder_NullArguments_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PlcClientFactoryBuilder.Create().WithCreator(null!));
        Assert.Throws<ArgumentNullException>(() => PlcClientFactoryBuilder.Create().WithLoggerFactory(null!));
    }

    [Fact(Timeout = 20000)]
    public void Builder_TwoCreatorsForOneConfigType_ThrowsWhenBuilt()
    {
        var builder = PlcClientFactoryBuilder.Create().WithCreator(new StubCreator()).WithCreator(new StubCreator());

        Assert.Throws<ArgumentException>(() => builder.Build());
    }

    [Fact(Timeout = 20000)]
    public void AddPlcClients_RegistersOneFactoryThatUsesTheRegisteredCreators()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPlcClientCreator>(new StubCreator());
        services.AddPlcClients();

        using ServiceProvider provider = services.BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        Assert.Same(factory, provider.GetRequiredService<PlcClientFactory>());
        Assert.IsType<FakePlcClient>(factory.GetOrAdd("line", new StubConfig()));
    }

    [Fact(Timeout = 20000)]
    public void AddPlcClients_CalledTwice_RegistersTheFactoryOnce()
    {
        var services = new ServiceCollection();

        services.AddPlcClients().AddPlcClients();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PlcClientFactory));
    }

    [Fact(Timeout = 20000)]
    public void AddPlcClients_WithoutCreators_CreatesAFactoryThatRejectsEveryConfig()
    {
        var services = new ServiceCollection();
        services.AddPlcClients();

        using ServiceProvider provider = services.BuildServiceProvider();
        PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();

        Assert.Throws<NotSupportedException>(() => factory.GetOrAdd("line", new StubConfig()));
    }

    [Fact(Timeout = 20000)]
    public void AddPlcClients_NullServices_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddPlcClients());
    }
}
