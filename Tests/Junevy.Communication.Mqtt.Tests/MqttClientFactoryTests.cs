using Junevy.Communication.Mqtt.DependencyInjection;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt.Tests;

public sealed class MqttClientFactoryTests
{
    private const string AppsettingsJson = """
        {
          "Host": "broker.local",
          "Port": 8883,
          "ClientId": "plc-1",
          "UserName": "device",
          "Password": "s3cret",
          "ProtocolVersion": "V500",
          "CleanSession": false,
          "SessionExpiryInterval": 3600,
          "KeepAliveSeconds": 30,
          "Timeout": 8000,
          "UseTls": true,
          "AllowUntrustedCertificates": false,
          "AutoReconnect": false,
          "ReconnectInterval": 2000,
          "MaxReconnectInterval": 60000,
          "WillTopic": "devices/plc-1/status",
          "WillPayload": "offline",
          "WillQos": "AtLeastOnce",
          "WillRetain": true
        }
        """;
    [Fact]
    public void GetOrAdd_ReturnsTheSameInstanceForTheSameName()
    {
        using var factory = new MqttClientFactory();
        MqttClientOptions options = MqttTestOptions.Valid(1883, "factory-1");

        MqttClient first = factory.GetOrAdd("line1", options);
        MqttClient second = factory.GetOrAdd("line1", MqttTestOptions.Valid(1883, "other"));

        Assert.Same(first, second);
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public void GetOrAdd_KeepsDifferentNamesApart()
    {
        using var factory = new MqttClientFactory();

        MqttClient first = factory.GetOrAdd("line1", MqttTestOptions.Valid(1883, "a"));
        MqttClient second = factory.GetOrAdd("line2", MqttTestOptions.Valid(1883, "b"));

        Assert.NotSame(first, second);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public void GetOrAdd_NullOptions_Throws()
    {
        using var factory = new MqttClientFactory();

        Assert.Throws<ArgumentNullException>("options", () => factory.GetOrAdd("line1", null!));
    }

    [Fact]
    public void GetOrAdd_InvalidOptions_ThrowsWithThePropertyName()
    {
        using var factory = new MqttClientFactory();
        MqttClientOptions options = MqttTestOptions.Valid(1883, "a");
        options.KeepAliveSeconds = -1;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => factory.GetOrAdd("line1", options));

        Assert.Equal("options.KeepAliveSeconds", error.ParamName);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task Dispose_ReleasesTheClients()
    {
        var factory = new MqttClientFactory();
        MqttClient client = factory.GetOrAdd("line1", MqttTestOptions.Valid(1883, "a"));

        factory.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheClients()
    {
        var factory = new MqttClientFactory();
        MqttClient client = factory.GetOrAdd("line1", MqttTestOptions.Valid(1883, "a"));

        await factory.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public void GetOrAdd_AfterDispose_Throws()
    {
        var factory = new MqttClientFactory();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("line1", MqttTestOptions.Valid(1883, "a")));
    }

    [Fact]
    public async Task GetOrAdd_TakesASnapshotOfTheOptions()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var factory = new MqttClientFactory();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "snapshot");
        options.KeepAliveSeconds = 30;
        MqttClient client = factory.GetOrAdd("line1", options);

        // 创建之后再改选项对象，不应影响已创建的客户端（P6-C9 的快照）。
        options.Port = 1;
        options.ClientId = "changed";
        options.KeepAliveSeconds = 99;

        await client.ConnectAndAssertAsync();
        await Polling.WaitUntilAsync(() => broker.Connects.Count == 1);

        ObservedConnect connect = Assert.Single(broker.Connects);
        Assert.Equal("snapshot", connect.ClientId);
        Assert.Equal((ushort)30, connect.KeepAliveSeconds);
    }

    [Fact]
    public void Options_BindFromAnAppsettingsShapedJson()
    {
        // 走 appsettings 真正用的那条路：IConfiguration.Bind（枚举按名字转换，不是按数字）。
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AppsettingsJson)))
            .Build();

        var options = new MqttClientOptions();
        configuration.Bind(options);

        Assert.Equal("broker.local", options.Host);
        Assert.Equal(8883, options.Port);
        Assert.Equal("plc-1", options.ClientId);
        Assert.Equal("device", options.UserName);
        Assert.Equal("s3cret", options.Password);
        Assert.Equal(MqttProtocolVersion.V500, options.ProtocolVersion);
        Assert.False(options.CleanSession);
        Assert.Equal(3600u, options.SessionExpiryInterval);
        Assert.Equal(30, options.KeepAliveSeconds);
        Assert.Equal(8000, options.Timeout);
        Assert.True(options.UseTls);
        Assert.False(options.AutoReconnect);
        Assert.Equal(2000, options.ReconnectInterval);
        Assert.Equal(60000, options.MaxReconnectInterval);
        Assert.Equal("devices/plc-1/status", options.WillTopic);
        Assert.Equal("offline", options.WillPayload);
        Assert.Equal(MqttQos.AtLeastOnce, options.WillQos);
        Assert.True(options.WillRetain);

        // 绑定出来的选项必须能通过校验，否则 appsettings 配错了要等到运行时才发现。
        MqttOptionsValidator.Validate(options);
    }
}

public sealed class MqttDependencyInjectionTests
{
    [Fact]
    public void AddMqttClients_RegistersASingletonFactory()
    {
        var services = new ServiceCollection();

        services.AddMqttClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<MqttClientFactory>(), provider.GetRequiredService<MqttClientFactory>());
    }

    [Fact]
    public void AddMqttClients_CanBeCalledTwice()
    {
        var services = new ServiceCollection();

        services.AddMqttClients();
        services.AddMqttClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<MqttClientFactory>());
    }

    [Fact]
    public void AddMqttClients_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>("services", () => ((IServiceCollection)null!).AddMqttClients());
    }

    [Fact]
    public void AddMqttClients_ResolvesAWorkingClient()
    {
        var services = new ServiceCollection();
        services.AddMqttClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        MqttClientFactory factory = provider.GetRequiredService<MqttClientFactory>();
        MqttClient client = factory.GetOrAdd("line1", MqttTestOptions.Valid(1883, "di-1"));

        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        Assert.Same(client, factory.GetOrAdd("line1", MqttTestOptions.Valid(1883, "di-1")));
    }

    [Fact]
    public void AddMqttClients_PassesTheContainerLoggerFactoryThrough()
    {
        var logger = new TestLogger();
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(logger);
        services.AddMqttClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        MqttClientOptions options = MqttTestOptions.Valid(1883, "di-2");
        options.AllowUntrustedCertificates = true;
        provider.GetRequiredService<MqttClientFactory>().GetOrAdd("line1", options);

        // 容器里的日志工厂被交给了客户端，因此 Create 记的告警能到记录器。
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("AllowUntrustedCertificates", StringComparison.Ordinal));
    }
}