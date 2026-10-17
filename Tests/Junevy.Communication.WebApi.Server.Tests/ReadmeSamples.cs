using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Server;

namespace Junevy.Communication.WebApi.Server.Tests.ReadmeSamples;

// 本文件中 README-SAMPLE-BEGIN / README-SAMPLE-END 之间的代码，与 Junevy.Communication.WebApi.Server/README.md 里的示例逐字相同。
// ReadmeSampleTests 会检查两者保持一致，并编译、运行这些示例：改这里就要同步改 README（反之亦然）。

// README-SAMPLE-BEGIN order-model
public sealed class Order
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
// README-SAMPLE-END order-model

// README-SAMPLE-BEGIN quick-start
public static class OrderService
{
    public static async Task<WebApiServer> StartAsync(string url)
    {
        var server = new WebApiServer("mes", new WebApiServerConfig { Urls = new[] { url } });
        server.MapGet<Order>("/orders/{id:int}", (context, _) =>
            Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = int.Parse(context.RouteValues["id"]) })));
        server.MapPost<Order, Order>("/orders", (order, _, _) =>
            Task.FromResult(WebApiServerResponse<Order>.Ok(order, 201)));

        CommResult started = await server.StartAsync();
        if (!started.IsSuccess)
            throw new InvalidOperationException(started.ErrorMessage);

        return server;
    }
}
// README-SAMPLE-END quick-start

// README-SAMPLE-BEGIN envelope
public static class EnvelopeService
{
    public static WebApiServer Create(string url)
    {
        var config = new WebApiServerConfig
        {
            Urls = new[] { url },
            Envelope = new ResponseEnvelopeOptions
            {
                Enabled = true,
                CodePath = "result.code",
                MessagePath = "result.msg",
                DataPath = "result.data",
                SuccessCodes = new[] { "0" },
            },
        };
        var server = new WebApiServer("mes", config);
        server.MapPost<Order, Order>("/orders", (order, _, _) => Task.FromResult(order.Id > 0
            ? WebApiServerResponse<Order>.Ok(order, 201)
            : WebApiServerResponse<Order>.Fail("E100", "the order id must be positive")));
        return server;
    }
}
// README-SAMPLE-END envelope

// README-SAMPLE-BEGIN interceptor
public sealed class RequireDeviceKey : WebApiServerInterceptor
{
    public override Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.Headers.ContainsKey("X-Device-Key"))
            context.Reject(403, "E403", "the device key is missing");
        return Task.CompletedTask;
    }
}
// README-SAMPLE-END interceptor

// README-SAMPLE-BEGIN deduplication
public static class DeduplicatedService
{
    public static WebApiServer Create(string url)
    {
        var config = new WebApiServerConfig
        {
            Urls = new[] { url },
            Deduplication = new WebApiServerDeduplicationOptions { Enabled = true },
        };
        var server = new WebApiServer("mes", config);
        server.MapPost<Order, Order>("/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order, 201)));
        return server;
    }
}
// README-SAMPLE-END deduplication
