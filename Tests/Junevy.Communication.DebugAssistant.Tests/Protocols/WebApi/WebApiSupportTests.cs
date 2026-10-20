using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Server;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.WebApi;

/// <summary>
/// WebApi 三个页的纯逻辑（计划第 7.9–7.11 节）：路由与 JSON 的校验、列表与行的解析、状态与时间线的显示文字、认证参数到库选项的映射。
/// </summary>
public sealed class WebApiSupportTests
{
    private static ServerRouteSettings Route(
        string method = "GET",
        string template = "/hello",
        ServerRouteKind kind = ServerRouteKind.Fixed,
        string? json = "{}",
        string? code = null,
        string? message = null,
        int? status = null,
        int delay = 0) => new(method, template, kind, json, code, message, status, delay);

    [Fact]
    public void RouteProblem_RefusesAnUnsupportedMethod()
    {
        Assert.Equal("方法只能是 GET、POST、PUT 或 DELETE。", WebApiSupport.RouteProblem(Route(method: "PATCH")));
    }

    [Fact]
    public void RouteProblem_RefusesATemplateWithoutASlash()
    {
        Assert.Equal("路由模板必须以 / 开头，例如 /orders/{id:int}。", WebApiSupport.RouteProblem(Route(template: "hello")));
    }

    [Fact]
    public void RouteProblem_RefusesAFixedResponseWithoutJson_OrWithInvalidJson()
    {
        Assert.Equal("固定 JSON 的响应需要填写 JSON 正文。", WebApiSupport.RouteProblem(Route(json: null)));
        Assert.StartsWith("JSON 无效：", WebApiSupport.RouteProblem(Route(json: "{")));
    }

    [Fact]
    public void RouteProblem_RefusesAFixedResponseWithAFailureStatus()
    {
        Assert.Equal("状态码必须在 200 到 299 之间。", WebApiSupport.RouteProblem(Route(status: 404)));
    }

    [Fact]
    public void RouteProblem_RefusesAStatusRouteWithoutAStatusCode()
    {
        Assert.Equal("指定状态码的响应需要填写状态码。", WebApiSupport.RouteProblem(Route(kind: ServerRouteKind.Status, json: null)));
    }

    [Fact]
    public void RouteProblem_RefusesABusinessFailureWithoutCodeOrMessage_AndOutOfRangeStatus()
    {
        Assert.Equal("业务失败需要填写业务码。", WebApiSupport.RouteProblem(Route(kind: ServerRouteKind.BusinessFailure, json: null, message: "m")));
        Assert.Equal("业务失败需要填写消息。", WebApiSupport.RouteProblem(Route(kind: ServerRouteKind.BusinessFailure, json: null, code: "E1")));
        Assert.Equal("状态码必须在 200 到 599 之间。", WebApiSupport.RouteProblem(Route(kind: ServerRouteKind.BusinessFailure, json: null, code: "E1", message: "m", status: 600)));
    }

    [Fact]
    public void RouteProblem_RefusesANegativeDelay()
    {
        Assert.Equal("延迟不能为负数。", WebApiSupport.RouteProblem(Route(delay: -1)));
    }

    [Fact]
    public void RouteProblem_AcceptsTheValidKinds()
    {
        Assert.Null(WebApiSupport.RouteProblem(Route()));
        Assert.Null(WebApiSupport.RouteProblem(Route(method: "POST", template: "/reports", kind: ServerRouteKind.Echo, json: null, status: 201)));
        Assert.Null(WebApiSupport.RouteProblem(Route(kind: ServerRouteKind.Status, status: 202, json: null)));
        Assert.Null(WebApiSupport.RouteProblem(Route(kind: ServerRouteKind.BusinessFailure, json: null, code: "E100", message: "bad")));
    }

    [Fact]
    public void Pretty_IndentsJson_AndKeepsOtherText()
    {
        string pretty = WebApiSupport.Pretty("{\"a\":1}");
        Assert.Contains("\"a\": 1", pretty);
        Assert.Contains(Environment.NewLine, pretty);
        Assert.Equal("not json", WebApiSupport.Pretty("not json"));
        Assert.Equal(string.Empty, WebApiSupport.Pretty(null));
    }

    [Fact]
    public void ParseList_SplitsOnCommasAndLines_AndTrims()
    {
        Assert.Equal(new[] { "a", "b", "c" }, WebApiSupport.ParseList(" a , b\nc ,, "));
    }

    [Fact]
    public void ParseLines_DropsBlankLines()
    {
        Assert.Equal(new[] { "http://a:1", "http://b:2" }, WebApiSupport.ParseLines("http://a:1\n\n http://b:2 "));
    }

    [Fact]
    public void StatusText_IsADashWithoutAStatus()
    {
        Assert.Equal(("—", "201"), (WebApiSupport.StatusText(null), WebApiSupport.StatusText(201)));
    }

    [Fact]
    public void TimelineRow_OmitsTheAttempt_ForEventsThatHaveNone()
    {
        DateTimeOffset now = new(2026, 10, 20, 9, 30, 15, 250, TimeSpan.Zero);

        Assert.Equal(("09:30:15.250", string.Empty), (WebApiSupport.TimelineRow(now, "可用性", 0, string.Empty, "x").Time, WebApiSupport.TimelineRow(now, "可用性", 0, string.Empty, "x").Attempt));
        Assert.Equal("2", WebApiSupport.TimelineRow(now, "发送", 2, string.Empty, "GET /hello").Attempt);
    }

    [Fact]
    public void Texts_AreChinese()
    {
        Assert.Equal(("在线", "离线", "未知"), (WebApiSupport.AvailabilityText(WebApiAvailability.Online), WebApiSupport.AvailabilityText(WebApiAvailability.Offline), WebApiSupport.AvailabilityText(WebApiAvailability.Unknown)));
        Assert.Equal(("成功", "去重重放", "超时"), (WebApiSupport.OutcomeText(WebApiServerOutcome.Succeeded), WebApiSupport.OutcomeText(WebApiServerOutcome.Replayed), WebApiSupport.OutcomeText(WebApiServerOutcome.TimedOut)));
        Assert.Equal(("运行中", "已停止"), (WebApiSupport.ServerStateText(WebApiServerState.Running), WebApiSupport.ServerStateText(WebApiServerState.Stopped)));
    }

    [Fact]
    public void AuthSettings_MapOnlyVariableNames_AndBlankOnesToNull()
    {
        var settings = new WebApiAuthSettings(WebApiAuthScheme.Bearer, " ", null, "MES_TOKEN", "X-API-Key", "");

        WebApiAuthOptions options = WebApiSupport.ToAuthOptions(settings);

        Assert.Equal((WebApiAuthScheme.Bearer, null, "MES_TOKEN", null), (options.Scheme, options.UserNameVariable, options.TokenVariable, options.ApiKeyVariable));
    }

    [Fact]
    public void ServerProblem_NeedsAnAddressAndARoute()
    {
        var auth = new WebApiAuthSettings(WebApiAuthScheme.None, null, null, null, "X-API-Key", null);
        var envelope = new WebApiEnvelopeSettings(false, "code", "msg", "data", ["0"]);
        var dedup = new WebApiDeduplicationSettings(false, "Idempotency-Key", 600000, 1000);
        var logging = new WebApiLogSettings(false, [], []);

        WebApiServerSettings Settings(string[] urls, ServerRouteSettings[] routes) => new(
            urls, auth, null, null, 16, 30000, 1048576, 5000, envelope, 200, dedup, logging, routes);

        Assert.Equal("至少需要一个监听地址。", WebApiSupport.ServerProblem(Settings([], [Route()])));
        Assert.Equal("至少需要登记一条路由。", WebApiSupport.ServerProblem(Settings(["http://127.0.0.1:0"], [])));
        Assert.Null(WebApiSupport.ServerProblem(Settings(["http://127.0.0.1:0"], [Route()])));
    }
}
