using System.Net;
using System.Text;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 重复下发去重的端到端行为（P32-D11）：真实 Kestrel 上的重放、409、422、不缓存的结果、超时之后的处理中条目、窗口过期与条目上限。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class DeduplicationTests
{
    private const string Header = "Idempotency-Key";
    private const string Json = "{\"id\":1,\"name\":\"widget\"}";

    [Fact]
    public async Task Disabled_EveryRequestWithTheSameKey_RunsTheHandler()
    {
        int runs = 0;
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[] { OrderRoute(settings, () => Interlocked.Increment(ref runs)) });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.SendAsync(Post(server.BaseUrl, "k1", Json));
        await http.SendAsync(Post(server.BaseUrl, "k1", Json));

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Completed_IsReplayed_WithTheSameStatusTypeAndBody_AndTheHandlerRunsOnce()
    {
        int runs = 0;
        WebApiServerSettings settings = Enabled(config => config.Deduplication.Enabled = true);
        await using var server = new ServerUnderTest(settings, new[] { OrderRoute(settings, () => Interlocked.Increment(ref runs), status: 201) });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage first = await http.SendAsync(Post(server.BaseUrl, "k1", Json));
        string firstBody = await first.Content.ReadAsStringAsync();
        HttpResponseMessage replay = await http.SendAsync(Post(server.BaseUrl, "k1", Json));

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(first.Content.Headers.ContentType!.ToString(), replay.Content.Headers.ContentType!.ToString());
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1, runs);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 2);
        Assert.Equal(new[] { WebApiServerOutcome.Succeeded, WebApiServerOutcome.Replayed }, server.Completed.Select(completed => completed.Outcome));
    }

    [Fact]
    public async Task SameKey_WhileInProgress_IsA409_UntilTheFirstRequestFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebApiServerSettings settings = Enabled(config => config.Deduplication.Enabled = true);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", async (order, _, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return WebApiServerResponse<Order>.Ok(order);
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        Task<HttpResponseMessage> first = http.SendAsync(Post(server.BaseUrl, "k1", Json));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HttpResponseMessage second = await http.SendAsync(Post(server.BaseUrl, "k1", Json));
        release.SetResult();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    [Fact]
    public async Task SameKey_WithADifferentBody_IsA422()
    {
        int runs = 0;
        WebApiServerSettings settings = Enabled(config => config.Deduplication.Enabled = true);
        await using var server = new ServerUnderTest(settings, new[] { OrderRoute(settings, () => Interlocked.Increment(ref runs)) });
        await server.StartAsync();
        using var http = new HttpClient();
        await http.SendAsync(Post(server.BaseUrl, "k1", Json));

        HttpResponseMessage response = await http.SendAsync(Post(server.BaseUrl, "k1", "{\"id\":2,\"name\":\"other\"}"));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task RequestsWithoutTheHeader_DoNotParticipate()
    {
        int runs = 0;
        WebApiServerSettings settings = Enabled(config => config.Deduplication.Enabled = true);
        await using var server = new ServerUnderTest(settings, new[] { OrderRoute(settings, () => Interlocked.Increment(ref runs)) });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.PostAsync(server.BaseUrl + "/orders", new StringContent(Json, Encoding.UTF8, "application/json"));
        await http.PostAsync(server.BaseUrl + "/orders", new StringContent(Json, Encoding.UTF8, "application/json"));

        Assert.Equal(2, runs);
        Assert.Equal(0, server.Host.DeduplicationEntryCount);
    }

    [Fact]
    public async Task HandlerFailure_IsNotCached_SoARetryRunsTheHandlerAgain()
    {
        int runs = 0;
        WebApiServerSettings settings = Enabled(config => config.Deduplication.Enabled = true);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                    throw new InvalidOperationException("transient");
                return Task.FromResult(WebApiServerResponse<Order>.Ok(order));
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage failed = await http.SendAsync(Post(server.BaseUrl, "k1", Json));
        HttpResponseMessage retried = await http.SendAsync(Post(server.BaseUrl, "k1", Json));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task TimeoutWhileRunning_KeepsTheKeyInProgress_UntilTheHandlerFinishes_ThenReplaysItsResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        WebApiServerSettings settings = Enabled(config =>
        {
            config.RequestTimeout = 300;
            config.Deduplication.Enabled = true;
        });
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", async (order, _, _) =>
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult();
                await release.Task;
                return WebApiServerResponse<Order>.Ok(order, 202);
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage timedOut = await http.SendAsync(Post(server.BaseUrl, "k1", Json));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HttpResponseMessage whileRunning = await http.SendAsync(Post(server.BaseUrl, "k1", Json));
        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, whileRunning.StatusCode);
        Assert.Equal(1, server.Host.DeduplicationInFlightCount);

        release.SetResult();
        await Polling.WaitUntilAsync(() => server.Host.DeduplicationInFlightCount == 0);
        HttpResponseMessage replay = await http.SendAsync(Post(server.BaseUrl, "k1", Json));

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task AfterTheWindow_TheHandlerRunsAgain()
    {
        int runs = 0;
        var time = new ManualTimeProvider();
        WebApiServerSettings settings = SettingsFactory.Create(config =>
        {
            config.Deduplication.Enabled = true;
            config.Deduplication.Window = 1000;
        }, new WebApiServerComponents { TimeProvider = time });
        await using var server = new ServerUnderTest(settings, new[] { OrderRoute(settings, () => Interlocked.Increment(ref runs)) });
        await server.StartAsync();
        using var http = new HttpClient();
        await http.SendAsync(Post(server.BaseUrl, "k1", Json));

        time.Advance(TimeSpan.FromSeconds(2));
        await http.SendAsync(Post(server.BaseUrl, "k1", Json));

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task WhenTheCacheIsFull_TheOldestCompletedEntryIsEvicted_AndTheCountStaysWithinTheLimit()
    {
        int runs = 0;
        WebApiServerSettings settings = Enabled(config =>
        {
            config.Deduplication.Enabled = true;
            config.Deduplication.MaxEntries = 2;
        });
        await using var server = new ServerUnderTest(settings, new[] { OrderRoute(settings, () => Interlocked.Increment(ref runs)) });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.SendAsync(Post(server.BaseUrl, "a", Json));
        await http.SendAsync(Post(server.BaseUrl, "b", Json));
        await http.SendAsync(Post(server.BaseUrl, "c", Json));
        Assert.Equal(2, server.Host.DeduplicationEntryCount);

        await http.SendAsync(Post(server.BaseUrl, "c", Json));
        Assert.Equal(3, runs);
        await http.SendAsync(Post(server.BaseUrl, "a", Json));
        Assert.Equal(4, runs);
    }

    [Fact]
    public async Task WhenTheCacheIsFullOfInFlightRequests_TheNewKeyIsRefusedWith503()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebApiServerSettings settings = Enabled(config =>
        {
            config.Deduplication.Enabled = true;
            config.Deduplication.MaxEntries = 1;
        });
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", async (order, _, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return WebApiServerResponse<Order>.Ok(order);
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        Task<HttpResponseMessage> first = http.SendAsync(Post(server.BaseUrl, "a", Json));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HttpResponseMessage second = await http.SendAsync(Post(server.BaseUrl, "b", Json));
        release.SetResult();
        await first;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal("1", second.Headers.GetValues("Retry-After").Single());
    }

    private static WebApiServerSettings Enabled(Action<WebApiServerConfig> configure)
        => SettingsFactory.Create(configure);

    private static HttpRequestMessage Post(string baseUrl, string key, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Header, key);
        return request;
    }

    // 处理器计数一次并返回请求的数据；状态码默认 200。
    private static Pipeline.RouteDefinition OrderRoute(WebApiServerSettings settings, Action run, int? status = null)
        => TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) =>
        {
            run();
            return Task.FromResult(WebApiServerResponse<Order>.Ok(order, status));
        });
}
