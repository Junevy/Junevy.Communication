using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.DependencyInjection;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Ftp.Tests;

public sealed class FtpClientFactoryTests
{
    private const string AppsettingsJson = """
        {
          "Host": "files.local",
          "Port": 990,
          "UserName": "device",
          "Password": "s3cret",
          "Encryption": "Implicit",
          "AllowUntrustedCertificates": false,
          "EncodingName": "GBK",
          "ConnectTimeout": 8000,
          "ReadTimeout": 12000,
          "DataConnectionTimeout": 20000
        }
        """;

    [Fact]
    public void GetOrAdd_ReturnsTheSameInstanceForTheSameName()
    {
        using var factory = new FtpClientFactory();

        FtpClient first = factory.GetOrAdd("line1", FtpTestClients.ValidOptions());
        FtpClient second = factory.GetOrAdd("line1", FtpTestClients.ValidOptions(port: 2121));

        Assert.Same(first, second);
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public void GetOrAdd_KeepsDifferentNamesApart()
    {
        using var factory = new FtpClientFactory();

        FtpClient first = factory.GetOrAdd("line1", FtpTestClients.ValidOptions());
        FtpClient second = factory.GetOrAdd("line2", FtpTestClients.ValidOptions());

        Assert.NotSame(first, second);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public void GetOrAdd_NullOptions_Throws()
    {
        using var factory = new FtpClientFactory();

        Assert.Throws<ArgumentNullException>("options", () => factory.GetOrAdd("line1", null!));
    }

    [Fact]
    public void GetOrAdd_InvalidOptions_ThrowsWithThePropertyName()
    {
        using var factory = new FtpClientFactory();
        FtpClientOptions options = FtpTestClients.ValidOptions();
        options.Port = 0;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => factory.GetOrAdd("line1", options));

        Assert.Equal("options.Port", error.ParamName);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task Dispose_ReleasesTheClients()
    {
        var factory = new FtpClientFactory();
        FtpClient client = factory.GetOrAdd("line1", FtpTestClients.ValidOptions());

        factory.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheClients()
    {
        var factory = new FtpClientFactory();
        FtpClient client = factory.GetOrAdd("line1", FtpTestClients.ValidOptions());

        await factory.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public void GetOrAdd_AfterDispose_Throws()
    {
        var factory = new FtpClientFactory();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.GetOrAdd("line1", FtpTestClients.ValidOptions()));
    }

    [Fact]
    public async Task GetOrAdd_TakesASnapshotOfTheOptions()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        using var factory = new FtpClientFactory();
        FtpClientOptions options = FtpTestClients.OptionsFor(server);
        FtpClient client = factory.GetOrAdd("line1", options);

        // 创建之后再改选项对象，不应影响已创建的客户端（P6-C9 的快照）。
        options.Port = 1;
        options.Host = "changed.invalid";
        options.UserName = "someone-else";

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public void Options_BindFromAnAppsettingsShapedJson()
    {
        // 走 appsettings 真正用的那条路：IConfiguration.Bind（枚举按名字转换，不是按数字）。
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AppsettingsJson));
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonStream(json).Build();

        var options = new FtpClientOptions();
        configuration.Bind(options);

        Assert.Equal("files.local", options.Host);
        Assert.Equal(990, options.Port);
        Assert.Equal("device", options.UserName);
        Assert.Equal("s3cret", options.Password);
        Assert.Equal(FtpEncryption.Implicit, options.Encryption);
        Assert.False(options.AllowUntrustedCertificates);
        Assert.Equal("GBK", options.EncodingName);
        Assert.Equal(8000, options.ConnectTimeout);
        Assert.Equal(12000, options.ReadTimeout);
        Assert.Equal(20000, options.DataConnectionTimeout);

        // 绑定出来的选项必须能通过校验，否则 appsettings 配错了要等到运行时才发现。
        FtpClientOptionsValidator.Validate(options);
    }
}

public sealed class FtpDependencyInjectionTests
{
    [Fact]
    public void AddFtpClients_RegistersASingletonFactory()
    {
        var services = new ServiceCollection();

        services.AddFtpClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<FtpClientFactory>(), provider.GetRequiredService<FtpClientFactory>());
    }

    [Fact]
    public void AddFtpClients_CanBeCalledTwice()
    {
        var services = new ServiceCollection();

        services.AddFtpClients();
        services.AddFtpClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        // 重复注册不会产生第二个工厂。
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(FtpClientFactory)));
        Assert.NotNull(provider.GetRequiredService<FtpClientFactory>());
    }

    [Fact]
    public void AddFtpClients_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>("services", () => ((IServiceCollection)null!).AddFtpClients());
    }

    [Fact]
    public async Task AddFtpClients_ResolvesAClientThatConnects()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        var services = new ServiceCollection();
        services.AddFtpClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        FtpClient client = provider.GetRequiredService<FtpClientFactory>().GetOrAdd("line1", FtpTestClients.OptionsFor(server));
        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void AddFtpClients_PassesTheContainerLoggerFactoryThrough()
    {
        using var logger = new TestLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logger);
        services.AddFtpClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        FtpClientOptions options = FtpTestClients.ValidOptions();
        options.AllowUntrustedCertificates = true;
        provider.GetRequiredService<FtpClientFactory>().GetOrAdd("line1", options);

        // 容器里的日志工厂被交给了客户端，因此 Create 记的告警能到记录器。
        Assert.Contains(logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("AllowUntrustedCertificates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposingTheContainer_ReleasesTheClients()
    {
        var services = new ServiceCollection();
        services.AddFtpClients();
        ServiceProvider provider = services.BuildServiceProvider();
        FtpClient client = provider.GetRequiredService<FtpClientFactory>().GetOrAdd("line1", FtpTestClients.ValidOptions());

        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }
}
