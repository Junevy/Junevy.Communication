using System.Net;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Rest;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 与 3.1 客户端的真实往返（P32 Task 6）：本包的服务器与 <c>WebApiClient</c>（启用包络）经真实套接字通信。
/// 覆盖成功、业务失败、401 与去重开启时带幂等键的重复调用。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class ClientRoundTripTests
{
    [Fact]
    public async Task Success_ReturnsTheSameData()
    {
        await using var server = await StartServerAsync(config => config.Envelope = SettingsFactory.NestedEnvelope(), route => route
            .MapGet<Order>("/orders/{id:int}", (context, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = int.Parse(context.RouteValues["id"]), Name = "widget" }))));
        using var client = CreateClient(server.Urls[0]);

        WebApiResult<Order> result = await client.SendAsync<Order>(new WebApiRequest { Path = "orders/42" });

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Data!.Id);
        Assert.Equal("widget", result.Data.Name);
    }

    [Fact]
    public async Task BusinessFailure_ReturnsRemoteErrorWithTheBusinessCode()
    {
        await using var server = await StartServerAsync(config => config.Envelope = SettingsFactory.NestedEnvelope(), route => route
            .MapGet<Order>("/orders/{id:int}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Fail("E42", "rejected by MES"))));
        using var client = CreateClient(server.Urls[0]);

        WebApiResult<Order> result = await client.SendAsync<Order>(new WebApiRequest { Path = "orders/1" });

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("E42", result.RemoteCode);
    }

    [Fact]
    public async Task Unauthorized_ReturnsAuthenticationFailed()
    {
        using var variables = new TemporaryVariables();
        string user = variables.Set("alice");
        string password = variables.Set("s3cret");
        await using var server = await StartServerAsync(config =>
        {
            config.Envelope = SettingsFactory.NestedEnvelope();
            config.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = user, PasswordVariable = password };
        }, route => route.MapGet<Order>("/orders/{id:int}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))));
        using var client = CreateClient(server.Urls[0]);

        WebApiResult<Order> result = await client.SendAsync<Order>(new WebApiRequest { Path = "orders/1" });

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal((int)HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Deduplication_RunsTheHandlerOnce_ForARepeatedIdempotencyKey()
    {
        int runs = 0;
        await using var server = await StartServerAsync(config =>
        {
            config.Envelope = SettingsFactory.NestedEnvelope();
            config.Deduplication.Enabled = true;
        }, route => route.MapPost<Order, Order>("/orders", (order, _, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(WebApiServerResponse<Order>.Ok(order, 201));
        }));
        using var client = CreateClient(server.Urls[0]);
        var request = new WebApiRequest
        {
            Method = HttpMethod.Post,
            Path = "orders",
            Body = new Order { Id = 7, Name = "widget" },
            IdempotencyKey = "order-7",
        };

        WebApiResult<Order> first = await client.SendAsync<Order>(request);
        WebApiResult<Order> second = await client.SendAsync<Order>(request);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(7, second.Data!.Id);
        Assert.Equal(1, runs);
    }

    private static async Task<WebApiServer> StartServerAsync(Action<WebApiServerConfig> configure, Action<WebApiServer> routes)
    {
        var config = new WebApiServerConfig { Urls = new[] { "http://127.0.0.1:0" } };
        configure(config);
        var server = new WebApiServer("mes", config);
        routes(server);
        CommResult started = await server.StartAsync();
        Assert.True(started.IsSuccess, started.ErrorMessage);
        return server;
    }

    private static WebApiClient CreateClient(string baseUrl)
        => new WebApiClient("mes", new WebApiClientConfig { BaseUrl = baseUrl + "/", Envelope = SettingsFactory.NestedEnvelope() });
}
