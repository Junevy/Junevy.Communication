using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.WebApi;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.WebApi;

/// <summary>
/// REST 客户端页与服务端页在本进程内的端到端测试（计划 Task 18 第 2 条）：服务端页在回环地址（端口 0）上启动，
/// REST 客户端页经真实的套接字调用它。覆盖固定响应与回显、业务失败与信封、指定状态码、超过请求超时的 504、Bearer 认证、
/// 健康检查、只有带幂等键的 POST 才重试（503 来自并发上限）、去重的重放与 422。
/// </summary>
public sealed class WebApiEndToEndTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static WebApiServerSessionViewModel CreateServer(ManualUiDispatcher dispatcher) =>
        new(ProtocolCatalog.Get(ProtocolKind.HttpServer), FakeServices.Create(dispatcher));

    private static RestClientSessionViewModel CreateRest(ManualUiDispatcher dispatcher, string baseUrl)
    {
        var page = new RestClientSessionViewModel(ProtocolCatalog.Get(ProtocolKind.RestClient), FakeServices.Create(dispatcher));
        page.BaseUrl = baseUrl;
        return page;
    }

    private static void AddRoute(
        WebApiServerSessionViewModel server,
        string method,
        string template,
        ServerRouteKind kind,
        string? json = null,
        string? code = null,
        string? message = null,
        int? status = null,
        int delay = 0)
    {
        server.NewRouteMethod = method;
        server.NewRouteTemplate = template;
        server.NewRouteKind = kind;
        server.NewRouteJson = json ?? string.Empty;
        server.NewRouteCode = code ?? string.Empty;
        server.NewRouteMessage = message ?? string.Empty;
        server.NewRouteStatusCode = status;
        server.NewRouteDelayMs = delay;
        server.AddRouteCommand.Execute(null);
        Assert.StartsWith("已登记", server.RouteMessage);
    }

    private static async Task<string> StartAsync(WebApiServerSessionViewModel server)
    {
        await server.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Running, server.State);
        return server.BoundUrls;
    }

    [Fact]
    public async Task FixedRoute_AndEchoPost_RoundTrip_ThroughBothPages()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        string baseUrl = await StartAsync(server);
        await using var rest = CreateRest(dispatcher, baseUrl);
        await rest.ConnectCommand.ExecuteAsync(null);

        rest.Method = "GET";
        rest.Path = "hello";
        await rest.SendCommand.ExecuteAsync(null);
        Assert.Equal(("200", "成功"), (rest.ResponseStatus, rest.ResponseErrorKind));
        Assert.Contains("\"message\": \"hello\"", rest.ResponseData);

        rest.Method = "POST";
        rest.Path = "reports";
        rest.BodyText = "{\"orderNo\":\"A-1\"}";
        await rest.SendCommand.ExecuteAsync(null);
        Assert.Contains("\"orderNo\": \"A-1\"", rest.ResponseData);

        await Waiting.UntilAsync(() => server.RequestLog.Count == 2, Wait, dispatcher.RunPending);
        Assert.Equal(new[] { "成功", "成功" }, server.RequestLog.Select(row => row.Outcome));
    }

    [Fact]
    public async Task BusinessFailure_WithTheEnvelope_IsARemoteError_CarryingTheCode()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        server.EnvelopeEnabled = true;
        AddRoute(server, "POST", "/orders", ServerRouteKind.BusinessFailure, code: "E100", message: "the order id must be positive");
        string baseUrl = await StartAsync(server);
        await using var rest = CreateRest(dispatcher, baseUrl);
        rest.EnvelopeEnabled = true;
        await rest.ConnectCommand.ExecuteAsync(null);

        rest.Method = "POST";
        rest.Path = "orders";
        rest.BodyText = "{\"id\":0}";
        await rest.SendCommand.ExecuteAsync(null);

        Assert.Equal(("200", "E100", "对端返回错误"), (rest.ResponseStatus, rest.ResponseRemoteCode, rest.ResponseErrorKind));
        Assert.Contains("the order id must be positive", rest.ResponseMessage);
        await Waiting.UntilAsync(() => rest.Timeline.Any(row => row.Event == "完成"), Wait, dispatcher.RunPending);
        Assert.Contains(rest.Timeline, row => row.Event == "完成" && row.Detail.StartsWith("对端返回错误", StringComparison.Ordinal));
        await Waiting.UntilAsync(() => server.RequestLog.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal("业务失败", server.RequestLog[0].Outcome);
    }

    [Fact]
    public async Task ExplicitStatusCode_IsTheResponseStatus()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        AddRoute(server, "POST", "/items", ServerRouteKind.Status, json: "{\"id\":7}", status: 201);
        string baseUrl = await StartAsync(server);
        await using var rest = CreateRest(dispatcher, baseUrl);
        await rest.ConnectCommand.ExecuteAsync(null);

        rest.Method = "POST";
        rest.Path = "items";
        rest.BodyText = "{}";
        await rest.SendCommand.ExecuteAsync(null);

        Assert.Equal(("201", "成功"), (rest.ResponseStatus, rest.ResponseErrorKind));
        Assert.Contains("\"id\": 7", rest.ResponseData);
    }

    [Fact]
    public async Task AHandlerThatOutlastsTheRequestTimeout_IsSeenAs504()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        server.RequestTimeoutMs = 300;
        AddRoute(server, "GET", "/slow", ServerRouteKind.Fixed, json: "{\"late\":true}", delay: 5000);
        string baseUrl = await StartAsync(server);
        await using var rest = CreateRest(dispatcher, baseUrl);
        await rest.ConnectCommand.ExecuteAsync(null);

        rest.Method = "GET";
        rest.Path = "slow";
        await rest.SendCommand.ExecuteAsync(null);

        Assert.Equal(("504", "对端返回错误"), (rest.ResponseStatus, rest.ResponseErrorKind));
        await Waiting.UntilAsync(() => server.RequestLog.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal("超时", server.RequestLog[0].Outcome);
    }

    [Fact]
    public async Task BearerAuthentication_RefusesAnAnonymousCaller_AndAcceptsTheToken()
    {
        string variable = "JUNEVY_DA_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-token");
        try
        {
            var dispatcher = new ManualUiDispatcher();
            await using var server = CreateServer(dispatcher);
            server.AuthScheme = WebApiAuthScheme.Bearer;
            server.TokenVariable = variable;
            string baseUrl = await StartAsync(server);

            await using var anonymous = CreateRest(dispatcher, baseUrl);
            await anonymous.ConnectCommand.ExecuteAsync(null);
            anonymous.Method = "GET";
            anonymous.Path = "hello";
            await anonymous.SendCommand.ExecuteAsync(null);
            Assert.Equal(("401", "认证失败"), (anonymous.ResponseStatus, anonymous.ResponseErrorKind));

            await using var authorized = CreateRest(dispatcher, baseUrl);
            authorized.AuthScheme = WebApiAuthScheme.Bearer;
            authorized.TokenVariable = variable;
            await authorized.ConnectCommand.ExecuteAsync(null);
            authorized.Method = "GET";
            authorized.Path = "hello";
            await authorized.SendCommand.ExecuteAsync(null);
            Assert.Equal(("200", "成功"), (authorized.ResponseStatus, authorized.ResponseErrorKind));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task HealthCheck_TurnsTheAvailabilityOnline()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        AddRoute(server, "GET", "/health", ServerRouteKind.Fixed, json: "{\"status\":\"ok\"}");
        string baseUrl = await StartAsync(server);
        await using var rest = CreateRest(dispatcher, baseUrl);
        rest.HealthCheckPath = "health";
        rest.HealthCheckIntervalMs = 100;

        await rest.ConnectCommand.ExecuteAsync(null);

        await Waiting.UntilAsync(() => rest.AvailabilityText == "在线", Wait, dispatcher.RunPending);
    }

    [Fact]
    public async Task APost_IsRetriedOnlyWithAnIdempotencyKey_WhenTheServerIsBusy()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        server.MaxConcurrentRequests = 1;
        AddRoute(server, "GET", "/slow", ServerRouteKind.Fixed, json: "{}", delay: 1500);
        string baseUrl = await StartAsync(server);
        await using var occupier = CreateRest(dispatcher, baseUrl);
        await occupier.ConnectCommand.ExecuteAsync(null);
        occupier.Method = "GET";
        occupier.Path = "slow";
        Task slow = occupier.SendCommand.ExecuteAsync(null);
        await Task.Delay(300);

        // 并发上限为 1：慢请求占着唯一的位置，第一次尝试得到 503；带幂等键的 POST 会重试，直到慢请求结束。
        await using var caller = CreateRest(dispatcher, baseUrl);
        caller.RetryCount = 3;
        caller.RetryDelayMs = 100;
        await caller.ConnectCommand.ExecuteAsync(null);
        caller.Method = "POST";
        caller.Path = "reports";
        caller.BodyText = "{\"n\":1}";
        caller.IdempotencyKey = "retry-1";
        await caller.SendCommand.ExecuteAsync(null);
        await slow;

        Assert.Equal("200", caller.ResponseStatus);
        await Waiting.UntilAsync(() => caller.Timeline.Count(row => row.Event == "发送") >= 2, Wait, dispatcher.RunPending);
    }

    [Fact]
    public async Task APost_WithoutAnIdempotencyKey_IsNotRetried_WhenTheServerIsBusy()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        server.MaxConcurrentRequests = 1;
        AddRoute(server, "GET", "/slow", ServerRouteKind.Fixed, json: "{}", delay: 1500);
        string baseUrl = await StartAsync(server);
        await using var occupier = CreateRest(dispatcher, baseUrl);
        await occupier.ConnectCommand.ExecuteAsync(null);
        occupier.Method = "GET";
        occupier.Path = "slow";
        Task slow = occupier.SendCommand.ExecuteAsync(null);
        await Task.Delay(300);

        await using var caller = CreateRest(dispatcher, baseUrl);
        caller.RetryCount = 3;
        caller.RetryDelayMs = 100;
        await caller.ConnectCommand.ExecuteAsync(null);
        caller.Method = "POST";
        caller.Path = "reports";
        caller.BodyText = "{\"n\":1}";
        await caller.SendCommand.ExecuteAsync(null);
        await slow;

        Assert.Equal(("503", "对端返回错误"), (caller.ResponseStatus, caller.ResponseErrorKind));
        dispatcher.RunPending();
        Assert.Equal(1, caller.Timeline.Count(row => row.Event == "发送"));
    }

    [Fact]
    public async Task IdempotentReplay_ReturnsTheFirstResponse_AndAMismatchedBodyIs422()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var server = CreateServer(dispatcher);
        server.DeduplicationEnabled = true;
        AddRoute(server, "POST", "/orders", ServerRouteKind.Echo);
        string baseUrl = await StartAsync(server);
        await using var rest = CreateRest(dispatcher, baseUrl);
        await rest.ConnectCommand.ExecuteAsync(null);
        rest.Method = "POST";
        rest.Path = "orders";
        rest.IdempotencyKey = "dup-1";

        rest.BodyText = "{\"id\":5}";
        await rest.SendCommand.ExecuteAsync(null);
        Assert.Equal("200", rest.ResponseStatus);

        // 同一个键、同一个正文：服务端返回第一次的响应，处理器不再执行。
        await rest.SendCommand.ExecuteAsync(null);
        Assert.Equal("200", rest.ResponseStatus);
        await Waiting.UntilAsync(() => server.RequestLog.Count == 2, Wait, dispatcher.RunPending);
        Assert.Equal("去重重放", server.RequestLog[0].Outcome);

        // 同一个键、不同的正文：422。
        rest.BodyText = "{\"id\":6}";
        await rest.SendCommand.ExecuteAsync(null);
        Assert.Equal("422", rest.ResponseStatus);
    }
}
