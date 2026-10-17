using System.Text;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Logging;
using Junevy.Communication.WebApi.Server.Pipeline;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 每个请求的 Debug 日志（P32-D15，P3.2 审阅修正 R3）：脱敏的请求头与查询参数、只在 <c>LogBodies</c> 时记录的正文与截断、结束日志里的路由模板。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class RequestLogTests
{
    [Fact]
    public void Request_RedactsTheConfiguredHeadersAndQueryParameters_CaseInsensitively()
    {
        var logger = new TestLogger();
        var log = new RequestLog(new WebApiLogOptions(), logger);

        log.LogRequest(7, Raw(
            new Dictionary<string, string> { ["TOKEN"] = "q-secret", ["line"] = "3" },
            new Dictionary<string, string> { ["authorization"] = "Bearer h-secret", ["X-Line"] = "L1" }), Array.Empty<byte>());

        string message = Assert.Single(logger.GetEntries(LogLevel.Debug)).Message;
        Assert.Contains("POST /orders?TOKEN=***&line=3", message, StringComparison.Ordinal);
        Assert.Contains("authorization: ***", message, StringComparison.Ordinal);
        Assert.Contains("X-Line: L1", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bodies_AreLoggedOnlyWhenEnabled_AndCutAtTheConfiguredLength()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":1,\"name\":\"widget\"}");
        var quiet = new TestLogger();
        var verbose = new TestLogger();

        new RequestLog(new WebApiLogOptions(), quiet).LogRequest(1, Raw(), body);
        new RequestLog(new WebApiLogOptions { LogBodies = true, MaxLoggedBodyLength = 8 }, verbose).LogRequest(1, Raw(), body);

        Assert.DoesNotContain(quiet.Entries, entry => entry.Message.Contains("body", StringComparison.Ordinal));
        string logged = Assert.Single(verbose.Entries, entry => entry.Message.Contains("body", StringComparison.Ordinal)).Message;
        Assert.Contains("{\"id\":1,... (truncated, 24 characters in total)", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_TheRequestAndCompletionAreLogged_WithTheRouteTemplateAndBothBodies()
    {
        WebApiServerSettings settings = SettingsFactory.Create(config => config.Logging.LogBodies = true);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Put<Order, Order>(settings, "/orders/{id:int}", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = order.Id, Name = "stored" }))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.PutAsync(server.BaseUrl + "/orders/5?access_token=abc", new StringContent("{\"id\":5,\"name\":\"sent\"}", Encoding.UTF8, "application/json"));
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);

        string[] messages = server.Logger.GetEntries(LogLevel.Debug).Select(entry => entry.Message).ToArray();
        Assert.Contains(messages, message => message.Contains("PUT /orders/5?access_token=***", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("body: {\"id\":5,\"name\":\"sent\"}", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("PUT /orders/{id:int} finished with 200 (Succeeded)", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("response body: {\"id\":5,\"name\":\"stored\"}", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("abc", StringComparison.Ordinal));
    }

    private static RawRequest Raw(IReadOnlyDictionary<string, string>? query = null, IReadOnlyDictionary<string, string>? headers = null)
        => new RawRequest(
            "POST",
            "/orders",
            query ?? new Dictionary<string, string>(),
            headers ?? new Dictionary<string, string>(),
            "application/json",
            null,
            "/orders",
            new Dictionary<string, string>(),
            _ => Task.FromResult(Array.Empty<byte>()));
}
