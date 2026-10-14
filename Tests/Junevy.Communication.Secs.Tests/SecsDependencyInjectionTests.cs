using System.Text;
using Junevy.Communication.Secs.DependencyInjection;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 工厂与依赖注入（计划 Task 8，P51-D17）：<c>AddSecs</c> 以单例注册工厂，工厂按名称复用设备端实例、
/// 别名共享实例、移除与释放会释放设备端；appsettings 形状的 JSON 直接绑定到 <see cref="HsmsEquipmentOptions"/>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class SecsDependencyInjectionTests
{
    [Fact(Timeout = 20000)]
    public void AddSecs_CalledTwice_RegistersTheFactoryOnce()
    {
        var services = new ServiceCollection();

        services.AddSecs().AddSecs();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(HsmsEquipmentFactory));
    }

    [Fact(Timeout = 20000)]
    public void AddSecs_NullServices_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddSecs());
    }

    [Fact(Timeout = 20000)]
    public void AddSecs_WithoutALoggerFactory_StillCreatesTheFactory()
    {
        using ServiceProvider provider = new ServiceCollection().AddSecs().BuildServiceProvider();

        HsmsEquipmentFactory factory = provider.GetRequiredService<HsmsEquipmentFactory>();

        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_GetOrAddTwice_ReturnsTheSameEquipment()
    {
        using var factory = new HsmsEquipmentFactory();

        HsmsEquipment first = factory.GetOrAdd("line", HsmsEquipmentRig.FastOptions());
        HsmsEquipment second = factory.GetOrAdd("line", HsmsEquipmentRig.FastOptions());

        Assert.Same(first, second);
        Assert.Equal(1, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_AnExistingName_IgnoresTheSecondOptionsObject()
    {
        using var factory = new HsmsEquipmentFactory();
        HsmsEquipmentOptions first = HsmsEquipmentRig.FastOptions();
        first.DeviceId = HsmsEquipmentRig.DefaultDeviceId;

        HsmsEquipment equipment = factory.GetOrAdd("line", first);
        HsmsEquipment again = factory.GetOrAdd("line", HsmsEquipmentRig.FastOptions());

        Assert.Same(equipment, again);
    }

    [Fact(Timeout = 20000)]
    public void Factory_AnAlias_SharesTheInstanceAndIsRemovedOnItsOwn()
    {
        using var factory = new HsmsEquipmentFactory();
        HsmsEquipment equipment = factory.GetOrAdd("line", HsmsEquipmentRig.FastOptions());

        Assert.True(factory.RegisterAlias("line1", "line"));
        Assert.Same(equipment, factory.GetOrAdd("line1", HsmsEquipmentRig.FastOptions()));
        Assert.True(factory.RemoveAlias("line1"));
        Assert.Same(equipment, factory.GetOrAdd("line", HsmsEquipmentRig.FastOptions()));

        // 直接名称仍有效，设备端没有被别名移除带走。
        Assert.Equal(1, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task Factory_RemovingAnEquipment_DisposesIt()
    {
        using var factory = new HsmsEquipmentFactory();
        HsmsEquipment equipment = factory.GetOrAdd("line", HsmsEquipmentRig.FastOptions());

        Assert.True(factory.TryRemove("line"));

        Assert.Equal(0, factory.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => equipment.StartAsync());
    }

    [Fact(Timeout = 20000)]
    public async Task Factory_Disposing_TheFactory_DisposesEveryEquipment()
    {
        var factory = new HsmsEquipmentFactory();
        HsmsEquipment first = factory.GetOrAdd("line", HsmsEquipmentRig.FastOptions());
        HsmsEquipment second = factory.GetOrAdd("line2", HsmsEquipmentRig.FastOptions());

        factory.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.StartAsync());
        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("line3", HsmsEquipmentRig.FastOptions()));
    }

    [Fact(Timeout = 20000)]
    public void Factory_InvalidOptions_ThrowAndRegisterNothing()
    {
        using var factory = new HsmsEquipmentFactory();
        var invalid = HsmsEquipmentRig.FastOptions();
        invalid.Port = 0;

        Assert.Throws<ArgumentException>(() => factory.GetOrAdd("line", invalid));
        Assert.Equal(0, factory.Count);
    }

    [Fact(Timeout = 20000)]
    public void Factory_NullOptions_ThrowArgumentNullException()
    {
        using var factory = new HsmsEquipmentFactory();

        Assert.Throws<ArgumentNullException>(() => factory.GetOrAdd("line", null!));
    }

    [Fact(Timeout = 30000)]
    public async Task AFactoryCreatedEquipment_CanBeStartedAndSelected()
    {
        using ServiceProvider provider = new ServiceCollection().AddSecs().BuildServiceProvider();
        HsmsEquipmentFactory factory = provider.GetRequiredService<HsmsEquipmentFactory>();
        HsmsEquipmentOptions options = HsmsEquipmentRig.FastOptions();

        HsmsEquipment equipment = factory.GetOrAdd("line", options);
        Assert.True((await equipment.StartAsync()).IsSuccess);

        await using HsmsHostConnector host = await HsmsEquipmentRig.SelectAsync(equipment, options.Port);

        Assert.Equal(HsmsState.Selected, equipment.State);
    }

    [Fact(Timeout = 20000)]
    public void AppsettingsShape_BindsToHsmsEquipmentOptions()
    {
        const string json = """
            {
              "Secs": {
                "Mode": "Active",
                "Host": "192.168.0.10",
                "Port": 5000,
                "DeviceId": 1,
                "T3": 45000,
                "T5": 10000,
                "T6": 5000,
                "T7": 10000,
                "T8": 5000,
                "LinktestInterval": 30000,
                "MaxMessageLength": 1048576
              }
            }
            """;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        HsmsEquipmentOptions bound = configuration.GetSection("Secs").Get<HsmsEquipmentOptions>()!;

        Assert.Equal(HsmsConnectionMode.Active, bound.Mode);
        Assert.Equal("192.168.0.10", bound.Host);
        Assert.Equal(5000, bound.Port);
        Assert.Equal((ushort)1, bound.DeviceId);
        Assert.Equal(45000, bound.T3);
        Assert.Equal(30000, bound.LinktestInterval);
        Assert.Equal(1048576, bound.MaxMessageLength);

        // 绑定出来的是能直接用的选项：经工厂拿到设备端（不建立连接）。
        using var factory = new HsmsEquipmentFactory();
        using HsmsEquipment equipment = factory.GetOrAdd("line", bound);
        Assert.Equal(HsmsState.NotConnected, equipment.State);
    }
}