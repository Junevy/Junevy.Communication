using System.Net;
using System.Text;
using System.Text.Json;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 入口、路由与框架的 404 / 405（P32 Task 3，P32-D5）：真实 Kestrel 上的往返、模板约束、字面段优先、事件的先后与路由模板。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class EntryAndRoutingTests
{
    [Fact]
    public async Task FourMethods_EachReachItsRouteAndReturnItsResponse()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders/{id:int}", (context, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = int.Parse(context.RouteValues["id"]) }))),
            TestRoutes.Post<Order, Order>(settings, "/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order, 201))),
            TestRoutes.Put<Order, Order>(settings, "/orders/{id:int}", (order, context, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = order.Id + int.Parse(context.RouteValues["id"]), Name = order.Name }))),
            TestRoutes.Delete<Order>(settings, "/orders/{id:int}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(null, 204))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage get = await http.GetAsync(server.BaseUrl + "/orders/42");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(42, (await ReadOrderAsync(get)).Id);

        HttpResponseMessage post = await http.PostAsync(server.BaseUrl + "/orders", JsonContent(new Order { Id = 1, Name = "a" }));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        HttpResponseMessage put = await http.PutAsync(server.BaseUrl + "/orders/10", JsonContent(new Order { Id = 5, Name = "b" }));
        Assert.Equal(15, (await ReadOrderAsync(put)).Id);

        HttpResponseMessage delete = await http.DeleteAsync(server.BaseUrl + "/orders/42");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task TemplateConstraint_RejectsAValueThatIsNotAnInteger_WithA404()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders/{id:int}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/orders/abc");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPath_IsAProblemDetailsWithStatus404()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("404", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WrongMethod_IsA405WithTheAllowedMethods()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders/{id:int}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.PostAsync(server.BaseUrl + "/orders/42", new ByteArrayContent(Array.Empty<byte>()));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task LiteralSegment_WinsOverAParameter()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders/{id}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Name = "parameter" }))),
            TestRoutes.Get<Order>(settings, "/orders/special", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Name = "literal" }))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/orders/special");

        Assert.Equal("literal", (await ReadOrderAsync(response)).Name);
    }

    [Fact]
    public async Task RequestReceived_PrecedesRequestCompleted_AndCompletedCarriesTheRouteTemplate()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders/{id:int}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        var order = new List<string>();
        server.Events.RequestReceived += (_, args) =>
        {
            lock (order)
                order.Add("received " + args.Path);
        };
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/orders/7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);

        WebApiServerCompletedEventArgs completed = server.Completed[0];
        Assert.Equal("/orders/{id:int}", completed.RouteTemplate);
        Assert.Equal(200, completed.StatusCode);
        Assert.Equal(WebApiServerOutcome.Succeeded, completed.Outcome);
        Assert.True(completed.Elapsed > TimeSpan.Zero);
        lock (order)
            Assert.Equal(new[] { "received /orders/7" }, order);
    }

    [Fact]
    public async Task FrameworkRejection_IsCompletedWithoutARouteTemplate()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.GetAsync(server.BaseUrl + "/missing");
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);

        Assert.Null(server.Completed[0].RouteTemplate);
        Assert.Equal(404, server.Completed[0].StatusCode);
        Assert.Equal(WebApiServerOutcome.Rejected, server.Completed[0].Outcome);
    }

    [Fact]
    public async Task InFlightCount_ReturnsToZero_AfterTheRequestsFinish()
    {
        WebApiServerSettings settings = SettingsFactory.Create();
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/orders", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.GetAsync(server.BaseUrl + "/orders");

        await Polling.WaitUntilAsync(() => server.Host.InFlightCount == 0);
        Assert.Equal(0, server.Host.InFlightCount);
    }

    private static StringContent JsonContent(object value)
        => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<Order> ReadOrderAsync(HttpResponseMessage response)
        => JsonSerializer.Deserialize<Order>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
