namespace Junevy.Communication.OpcUa.Tests.TestSupport;

/// <summary>
/// 全测试程序集共用的默认测试服务器（计划第 1 节第 4 条）：默认配置下整个测试集只启动一次，
/// 因为 OPC UA 服务器首次启动要生成证书、很慢。证书目录每次运行一个唯一的临时目录，结束时删除。
/// 需要特殊配置（没有 None 端点、不允许匿名、操作上限很小）或需要停止、重启的用例各自启动实例。
/// </summary>
public sealed class OpcUaServerFixture : IAsyncLifetime
{
    private OpcUaTestServer? server;

    /// <summary>共用的服务器实例。</summary>
    public OpcUaTestServer Server => server ?? throw new InvalidOperationException("The shared test server is not running.");

    /// <summary>启动共用服务器。</summary>
    /// <returns>异步操作。</returns>
    public async Task InitializeAsync()
        => server = await OpcUaTestServer.StartAsync(new OpcUaTestServerOptions
        {
            EndpointUrl = OpcUaTestServerFactory.FreeEndpointUrl(),
            PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
        }).ConfigureAwait(false);

    /// <summary>停止共用服务器并清理证书目录。</summary>
    /// <returns>异步操作。</returns>
    public async Task DisposeAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            server = null;
        }
    }
}

/// <summary>需要共用服务器的用例所在的集合（整个测试集串行）。</summary>
[CollectionDefinition(Name)]
public sealed class OpcUaServerCollection : ICollectionFixture<OpcUaServerFixture>
{
    /// <summary>集合名。</summary>
    public const string Name = "OpcUaServer";
}

/// <summary>测试服务器的辅助：免费端口与唯一证书目录。</summary>
public static class OpcUaTestServerFactory
{
    /// <summary>
    /// 申请一个回环地址上的空闲端口并给出端点地址。
    /// </summary>
    /// <returns><c>opc.tcp://127.0.0.1:&lt;port&gt;/UA/</c>。</returns>
    public static string FreeEndpointUrl()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"opc.tcp://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/UA/";
    }

    /// <summary>
    /// 本次运行唯一的证书目录。
    /// </summary>
    /// <returns>临时目录路径；已创建。</returns>
    public static string UniquePkiRoot()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "junevy-opcua-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 启动一个"自己会用"的服务器实例：测试自己负责释放。
    /// </summary>
    /// <param name="configure">可选的配置回调。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时静默。</param>
    /// <returns>已启动的服务器。</returns>
    public static Task<OpcUaTestServer> StartOwnAsync(
        Action<OpcUaTestServerOptions>? configure = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        var options = new OpcUaTestServerOptions
        {
            EndpointUrl = FreeEndpointUrl(),
            PkiRoot = UniquePkiRoot(),
        };
        configure?.Invoke(options);
        return OpcUaTestServer.StartAsync(options, loggerFactory);
    }
}