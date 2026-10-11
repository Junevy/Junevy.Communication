using System.Net;
using System.Net.Http;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="HttpHandlerFactory"/> 的测试（P3-D12）：不自动重定向、自动解压、连接数与池化时间；绝不修改进程级静态设置（Q24）。
/// </summary>
public sealed class HttpHandlerFactoryTests
{
    private static readonly TimeSpan ExpectedLifetime = TimeSpan.FromMinutes(5);

    [Fact]
    public void Create_DoesNotFollowRedirectsAndDecompressesGzipAndDeflate()
    {
        using HttpMessageHandler handler = HttpHandlerFactory.Create(Settings(7), new Uri("http://127.0.0.1:59871/"));

#if NET8_0_OR_GREATER
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, sockets.AutomaticDecompression);
        Assert.Equal(7, sockets.MaxConnectionsPerServer);
        Assert.Equal(ExpectedLifetime, sockets.PooledConnectionLifetime);
#else
        var classic = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(classic.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, classic.AutomaticDecompression);
#endif
    }

#if !NET8_0_OR_GREATER
    [Fact]
    public void Create_OnNet472_ConfiguresThePerEndpointServicePointOnly()
    {
        var endpoint = new Uri("http://127.0.0.1:59872/");
        int processDefaultLimit = ServicePointManager.DefaultConnectionLimit;
        int processDefaultLease = ServicePointManager.DnsRefreshTimeout;

        using HttpMessageHandler handler = HttpHandlerFactory.Create(Settings(7), endpoint);

        ServicePoint servicePoint = ServicePointManager.FindServicePoint(endpoint);
        Assert.Equal(7, servicePoint.ConnectionLimit);
        Assert.Equal((int)ExpectedLifetime.TotalMilliseconds, servicePoint.ConnectionLeaseTimeout);
        Assert.Equal(processDefaultLimit, ServicePointManager.DefaultConnectionLimit);
        Assert.Equal(processDefaultLease, ServicePointManager.DnsRefreshTimeout);
    }
#endif

    private static EndpointSettings Settings(int maxConnections)
        => EndpointConfigRules.CreateSettings(new TestEndpointConfig { MaxConnectionsPerServer = maxConnections });
}
