using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 公开外观上的并发调用（P3.2 审阅修正，P32-D12）：释放与启动、停止的竞争，停止期间读取 <c>Urls</c>，以及释放不释放仍在用的锁。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class WebApiServerConcurrencyTests
{
    private const string Loopback = "http://127.0.0.1:0";

    [Fact]
    public async Task StartWaitingBehindDispose_ThrowsObjectDisposed_AndNothingKeepsListening()
    {
        WebApiServer server = await StartedServerAsync();
        string address = server.Urls[0];

        ValueTask disposing = server.DisposeAsync();
        Task<CommResult> starting = server.StartAsync();
        await disposing;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => starting);
        Assert.Equal(WebApiServerState.Stopped, server.State);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(address + "/x"));
    }

    [Fact]
    public async Task StopWaitingBehindDispose_CompletesWithoutThrowing()
    {
        WebApiServer server = await StartedServerAsync();

        ValueTask disposing = server.DisposeAsync();
        Task stopping = server.StopAsync();
        await disposing;

        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebApiServerState.Stopped, server.State);
    }

    [Fact]
    public async Task Urls_CanBeReadWhileTheServerStops()
    {
        for (int round = 0; round < 10; round++)
        {
            WebApiServer server = await StartedServerAsync();
            using var done = new CancellationTokenSource();
            Task reader = Task.Run(() =>
            {
                while (!done.IsCancellationRequested)
                    Assert.NotEmpty(server.Urls);
            });

            await server.StopAsync();
            done.Cancel();
            await reader;
            Assert.Equal(new[] { Loopback }, server.Urls);
            await server.DisposeAsync();
        }
    }

    private static async Task<WebApiServer> StartedServerAsync()
    {
        var server = new WebApiServer("test", new WebApiServerConfig { Urls = new[] { Loopback } });
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        Assert.True((await server.StartAsync()).IsSuccess);
        return server;
    }
}
