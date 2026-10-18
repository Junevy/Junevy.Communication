using System.Net;
using System.Net.Sockets;
using System.Text;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Pipeline;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 管线的阶段顺序与被摘出去的工作（P3.2 审阅修正）：认证先于解码；504 或对端断开之后迟到的结果不交给响应拦截器；
/// 处理器自己抛出的取消是 500；畸形的分块正文是 400（D-7）；对端断开后仍在运行的处理器记 Warning 并计入被摘出的数量（D-8）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class PipelineOrderTests
{
    [Theory]
    [InlineData("{ not json", "application/json")]
    [InlineData("{\"id\":1}", "text/plain")]
    public async Task UnauthenticatedRequest_Gets401_BeforeItsBodyIsDecoded(string body, string contentType)
    {
        int decodedRuns = 0;
        WebApiServerSettings settings = SettingsFactory.Create(components: new WebApiServerComponents { Authenticator = new RejectingAuthenticator() });
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) =>
            {
                Interlocked.Increment(ref decodedRuns);
                return Task.FromResult(WebApiServerResponse<Order>.Ok(order));
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.PostAsync(server.BaseUrl + "/orders", new StringContent(body, Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, decodedRuns);
    }

    [Fact]
    public async Task LateResultAfterA504_IsNotGivenToTheResponseInterceptors()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new RecordingInterceptor();
        var components = new WebApiServerComponents();
        components.Interceptors.Add(recorder);
        WebApiServerSettings settings = SettingsFactory.Create(config => config.RequestTimeout = 300, components);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/stubborn", async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700));
                finished.TrySetResult();
                return WebApiServerResponse<Order>.Ok(new Order());
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/stubborn");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Polling.WaitUntilAsync(() => server.Host.AbandonedCount == 0);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, recorder.RequestStages);
        Assert.Equal(0, recorder.ResponseStages);
    }

    [Fact]
    public async Task HandlerThrowingItsOwnCancellation_IsA500_ThatStillPassesTheResponseStage()
    {
        var recorder = new RecordingInterceptor();
        var components = new WebApiServerComponents();
        components.Interceptors.Add(recorder);
        WebApiServerSettings settings = SettingsFactory.Create(components: components);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/downstream", (_, _) => throw new TaskCanceledException("downstream call timed out")),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/downstream");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.HandlerFailed, server.Completed[0].Outcome);
        Assert.Equal(1, recorder.ResponseStages);
    }

    [Fact]
    public async Task MalformedChunkedBody_IsA400_NotADisconnect()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order))),
        });
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, new Uri(server.BaseUrl).Port);
        using NetworkStream stream = tcp.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "POST /orders HTTP/1.1\r\nHost: test\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\nZZ\r\n{}\r\n0\r\n\r\n"));

        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.Rejected, server.Completed[0].Outcome);
        Assert.Equal(400, server.Completed[0].StatusCode);
        Assert.IsType<RequestBodyMalformedException>(server.Completed[0].Exception);
    }

    [Fact]
    public async Task ClientDisconnecting_WhileTheHandlerIgnoresItsToken_LogsAWarning_AndCountsTheAbandonedHandler()
    {
        using var unobserved = new UnobservedTaskWatch();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/stubborn", async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("late failure");
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();
        using var cancel = new CancellationTokenSource();

        Task<HttpResponseMessage> request = http.GetAsync(server.BaseUrl + "/stubborn", cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Record.ExceptionAsync(() => request);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);

        Assert.Equal(WebApiServerOutcome.ClientDisconnected, server.Completed[0].Outcome);
        Assert.Contains(server.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("still running after the client disconnected", StringComparison.Ordinal));
        Assert.Equal(1, server.Host.AbandonedCount);
        Assert.Equal(0, server.Host.InFlightCount);

        release.SetResult();
        await Polling.WaitUntilAsync(() => server.Host.AbandonedCount == 0);
        await unobserved.AssertNoneAsync();
    }

    private sealed class RejectingAuthenticator : IWebApiRequestAuthenticator
    {
        public Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken)
            => Task.FromResult(WebApiAuthenticationResult.Fail("no credentials"));
    }

    private sealed class RecordingInterceptor : WebApiServerInterceptor
    {
        private int requestStages;
        private int responseStages;

        public int RequestStages => Volatile.Read(ref requestStages);

        public int ResponseStages => Volatile.Read(ref responseStages);

        public override Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestStages);
            return Task.CompletedTask;
        }

        public override Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref responseStages);
            return Task.CompletedTask;
        }
    }
}
