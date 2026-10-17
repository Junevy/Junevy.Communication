using System.Diagnostics;
using System.Globalization;
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
/// 并发上限、正文上限、超时与被摘出去的工作（P32 Task 3，P32-D8）。耗时断言使用区间，不使用固定的 Sleep。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class LimitsAndTimeoutTests
{
    [Fact]
    public async Task ConcurrencyLimit_RejectsTheExtraRequestWith503AndRetryAfter_WithoutQueueing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebApiServerSettings settings = SettingsFactory.Create(config => config.MaxConcurrentRequests = 1);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/slow", async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return WebApiServerResponse<Order>.Ok(new Order());
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        Task<HttpResponseMessage> first = http.GetAsync(server.BaseUrl + "/slow");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, server.Host.InFlightCount);

        HttpResponseMessage second = await http.GetAsync(server.BaseUrl + "/slow");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal("1", second.Headers.GetValues("Retry-After").Single());
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        await Polling.WaitUntilAsync(() => server.Host.InFlightCount == 0);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 2);
        Assert.Contains(server.Completed, completed => completed.StatusCode == 503 && completed.Outcome == WebApiServerOutcome.Rejected);
        Assert.Contains(server.Completed, completed => completed.StatusCode == 200 && completed.Outcome == WebApiServerOutcome.Succeeded);
    }

    [Fact]
    public async Task BodyTooLarge_IsRejectedWith413_AndReportsTheException()
    {
        WebApiServerSettings settings = SettingsFactory.Create(config => config.MaxRequestBodySize = 1024);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order))),
        });
        await server.StartAsync();
        using var http = new HttpClient();
        string large = "{\"id\":1,\"name\":\"" + new string('x', 4096) + "\"}";

        HttpResponseMessage response = await http.PostAsync(server.BaseUrl + "/orders", new StringContent(large, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.Rejected, server.Completed[0].Outcome);
        Assert.Equal(413, server.Completed[0].StatusCode);
        Assert.IsType<RequestBodyTooLargeException>(server.Completed[0].Exception);
    }

    [Fact]
    public async Task BodyNotReceivedInTime_IsRejectedWith408()
    {
        WebApiServerSettings settings = SettingsFactory.Create(config => config.RequestTimeout = 400);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order))),
        });
        await server.StartAsync();
        int port = new Uri(server.BaseUrl).Port;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = tcp.GetStream();
        byte[] head = Encoding.ASCII.GetBytes("POST /orders HTTP/1.1\r\nHost: test\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n");
        await stream.WriteAsync(head);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        string statusLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? string.Empty;

        Assert.StartsWith("HTTP/1.1 408", statusLine, StringComparison.Ordinal);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(408, server.Completed[0].StatusCode);
        Assert.Equal(WebApiServerOutcome.Rejected, server.Completed[0].Outcome);
    }

    [Fact]
    public async Task HandlerPastTheTimeout_IsA504Promptly()
    {
        WebApiServerSettings settings = SettingsFactory.Create(config => config.RequestTimeout = 300);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/slow", async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return WebApiServerResponse<Order>.Ok(new Order());
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        var clock = Stopwatch.StartNew();
        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/slow");
        clock.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.TimedOut, server.Completed[0].Outcome);
    }

    [Fact]
    public async Task HandlerIgnoringTheToken_Returns504_AndItsLateResultIsDiscardedWithoutAnUnobservedException()
    {
        using var unobserved = new UnobservedTaskWatch();
        WebApiServerSettings settings = SettingsFactory.Create(config => config.RequestTimeout = 300);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/stubborn", async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(900));
                throw new InvalidOperationException("late failure");
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/stubborn");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains(server.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("still running after the timeout", StringComparison.Ordinal));
        await Polling.WaitUntilAsync(() => server.Logger.GetEntries(LogLevel.Debug).Any(entry => entry.Message.Contains("finished after it was abandoned", StringComparison.Ordinal)));
        await Task.Delay(50);
        Assert.Single(server.Completed);
        Assert.Equal(WebApiServerOutcome.TimedOut, server.Completed[0].Outcome);
        await unobserved.AssertNoneAsync();
    }

    [Fact]
    public async Task StopPastItsDeadline_AbortsTheInFlightRequest_AndTheServerSideCountReturnsToZero()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/slow", async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return WebApiServerResponse<Order>.Ok(new Order());
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();
        Task<HttpResponseMessage> request = http.GetAsync(server.BaseUrl + "/slow");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, server.Host.InFlightCount);

        await server.StopAsync(TimeSpan.FromMilliseconds(300));

        await Polling.WaitUntilAsync(() => server.Host.InFlightCount == 0);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.ClientDisconnected, server.Completed[0].Outcome);
        Assert.Equal(0, server.Host.AbandonedCount);
        await Record.ExceptionAsync(() => request);
    }

    [Fact]
    public async Task FailingSubscriber_DoesNotChangeTheResponseOrTheOtherSubscribers()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        int received = 0;
        server.Events.RequestCompleted += (_, _) => throw new InvalidOperationException("subscriber failure");
        server.Events.RequestCompleted += (_, _) => Interlocked.Increment(ref received);
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Polling.WaitUntilAsync(() => Volatile.Read(ref received) == 1);
        Assert.Contains(server.Logger.GetEntries(LogLevel.Error), entry => entry.Message.Contains("subscriber threw an exception", StringComparison.Ordinal));
    }
}
