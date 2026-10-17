using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Hosting;
using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Tests.TestSupport;

/// <summary>
/// 运行一个真实 Kestrel 实例（<c>http://127.0.0.1:0</c>）的测试辅助：收集 <c>RequestCompleted</c> 与日志，便于断言"恰好一次"。
/// </summary>
internal sealed class ServerUnderTest : IAsyncDisposable
{
    private readonly KestrelHost host;
    private readonly List<WebApiServerCompletedEventArgs> completed = new List<WebApiServerCompletedEventArgs>();
    private readonly object gate = new object();

    /// <summary>创建测试实例（尚未启动）。</summary>
    /// <param name="settings">已校验的配置快照。</param>
    /// <param name="routes">路由定义。</param>
    public ServerUnderTest(WebApiServerSettings settings, IEnumerable<RouteDefinition> routes)
    {
        Logger = new TestLogger();
        Events = new ServerEvents(Logger.CreateLogger("events"));
        Events.RequestCompleted += (_, args) =>
        {
            lock (gate)
                completed.Add(args);
        };
        host = KestrelHost.Create(settings, "test", routes, Events, Logger);
    }

    /// <summary>测试日志器（同时是调用方的日志工厂）。</summary>
    public TestLogger Logger { get; }

    /// <summary>事件派发器。</summary>
    public ServerEvents Events { get; }

    /// <summary>应用宿主，用于读取在途数。</summary>
    public KestrelHost Host => host;

    /// <summary>启动后的基地址，例如 <c>http://127.0.0.1:54321</c>（不含结尾的斜杠）。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>已收到的 <c>RequestCompleted</c>（快照，按触发顺序）。</summary>
    public IReadOnlyList<WebApiServerCompletedEventArgs> Completed
    {
        get
        {
            lock (gate)
                return completed.ToArray();
        }
    }

    /// <summary>启动应用并记录实际地址。</summary>
    public async Task StartAsync()
    {
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        BaseUrl = host.Addresses[0];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await host.DisposeAsync().ConfigureAwait(false);
}
