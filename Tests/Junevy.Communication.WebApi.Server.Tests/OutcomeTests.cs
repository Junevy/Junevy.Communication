using System.Net;
using System.Text;
using System.Text.Json;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 结果分类（P32 Task 3，第 16 节）：每种 <c>Outcome</c> 的状态码与正文，以及 <c>RequestCompleted</c> 恰好一次（Q26）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OutcomeTests
{
    [Fact]
    public async Task BusinessFailure_WithTheEnvelope_IsHttp200WithTheBusinessCode()
    {
        WebApiServerSettings settings = SettingsFactory.Create(config => config.Envelope = SettingsFactory.NestedEnvelope());
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/fail", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Fail("E42", "rejected"))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/fail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("E42", document.RootElement.GetProperty("result").GetProperty("code").GetString());
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.BusinessFailure, server.Completed[0].Outcome);
        Assert.Equal("E42", server.Completed[0].Code);
    }

    [Fact]
    public async Task MalformedBodies_AreRejectedWith400ForEmptyAnd415ForWrongContentType()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage empty = await http.PostAsync(server.BaseUrl + "/orders", new ByteArrayContent(Array.Empty<byte>()));
        HttpResponseMessage plain = await http.PostAsync(server.BaseUrl + "/orders", new StringContent("x", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 2);
        Assert.All(server.Completed, completed => Assert.Equal(WebApiServerOutcome.Rejected, completed.Outcome));
    }

    [Fact]
    public async Task HandlerException_IsA500WithoutDetails_AndTheExceptionIsLogged()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/boom", (_, _) => throw new InvalidOperationException("internal-secret-detail")),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("internal-secret-detail", body);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.HandlerFailed, server.Completed[0].Outcome);
        Assert.IsType<InvalidOperationException>(server.Completed[0].Exception);
        Assert.Contains(server.Logger.GetEntries(LogLevel.Error), entry => entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task ClientDisconnect_IsReportedOnce_WithoutAResponseOrAnError()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/slow", async (_, token) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (OperationCanceledException)
                {
                    sawAbort.TrySetResult();
                    throw;
                }

                return WebApiServerResponse<Order>.Ok(new Order());
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();
        using var cancel = new CancellationTokenSource();

        Task<HttpResponseMessage> request = http.GetAsync(server.BaseUrl + "/slow", cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await sawAbort.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.ClientDisconnected, server.Completed[0].Outcome);
        Assert.Equal(0, server.Completed[0].StatusCode);
    }

    [Fact]
    public async Task RequestCompleted_IsRaisedExactlyOnce_ForEachRequest()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.GetAsync(server.BaseUrl + "/orders");
        await http.GetAsync(server.BaseUrl + "/missing");
        await http.PostAsync(server.BaseUrl + "/orders", new StringContent("x", Encoding.UTF8, "text/plain"));

        await Polling.WaitUntilAsync(() => server.Completed.Count == 3);
        await Task.Delay(100);
        Assert.Equal(3, server.Completed.Count);
        Assert.Equal(3, server.Completed.Select(completed => completed.RequestId).Distinct().Count());
    }
}
