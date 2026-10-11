using System.Net;
using System.Net.Http;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 创建端点自己的 HTTP 处理器（内部，P3-D12）。这是 WebApi 里唯一含条件编译的文件。
/// 两个目标的共同点：不自动重定向（自动重定向会把 POST 静默改成 GET 并丢掉正文）、自动解压 GZip / Deflate、同一服务器的并发连接数来自配置。
/// 不修改任何进程级静态设置（<c>ServicePointManager</c> 的静态属性），net472 只设置该端点自己的 <see cref="ServicePoint"/>。
/// </summary>
internal static class HttpHandlerFactory
{
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 创建处理器，由调用方（端点）释放。
    /// </summary>
    /// <param name="settings">端点配置快照。</param>
    /// <param name="endpoint">端点的基地址（net472 用它定位该端点的 ServicePoint）。</param>
    /// <returns>HTTP 处理器。</returns>
    public static HttpMessageHandler Create(EndpointSettings settings, Uri endpoint)
    {
#if NET8_0_OR_GREATER
        // 连接最多复用 5 分钟，之后重新解析 DNS（MES 做了故障切换时才能连到新地址）。
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = PooledConnectionLifetime,
            MaxConnectionsPerServer = settings.MaxConnectionsPerServer,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
#else
        // net472 默认每主机只有 2 个连接；ServicePoint 按主机共享，因此这个设置也作用于同一主机上的其他代码（README 注明）。
        ServicePoint servicePoint = ServicePointManager.FindServicePoint(endpoint);
        servicePoint.ConnectionLimit = settings.MaxConnectionsPerServer;
        servicePoint.ConnectionLeaseTimeout = (int)PooledConnectionLifetime.TotalMilliseconds;

        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
#endif
    }
}
