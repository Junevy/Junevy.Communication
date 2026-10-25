using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.WebApi;

/// <summary>
/// WebApi 服务端页的视图模型（计划 Task 18 第 2 条的单元部分）：路由的登记与删除及不合法路由的原因、运行中不能修改路由、
/// 端口 0 时显示实际绑定的地址、模板冲突与没有路由时启动被拒绝。请求的端到端行为见 <c>WebApiEndToEndTests</c>。
/// </summary>
public sealed class WebApiServerSessionViewModelTests
{
    private static WebApiServerSessionViewModel CreatePage() =>
        new(ProtocolCatalog.Get(ProtocolKind.HttpServer), FakeServices.Create(new ManualUiDispatcher()));

    private static void Describe(WebApiServerSessionViewModel page, string method, string template, ServerRouteKind kind, string json, int? status = null)
    {
        page.NewRouteMethod = method;
        page.NewRouteTemplate = template;
        page.NewRouteKind = kind;
        page.NewRouteJson = json;
        page.NewRouteStatusCode = status;
    }

    [Fact]
    public async Task Page_StartsStopped_WithTwoDefaultRoutes()
    {
        await using var page = CreatePage();

        Assert.Equal((SessionState.NotConnected, "已停止", true, 2), (page.State, page.ServerStateText, page.IsEditable, page.Routes.Count));
    }

    [Fact]
    public async Task Routes_AddAndRemove_AndRefuseAnInvalidOne()
    {
        await using var page = CreatePage();

        Describe(page, "GET", "hello", ServerRouteKind.Fixed, "{}");
        page.AddRouteCommand.Execute(null);
        Assert.Equal("路由模板必须以 / 开头，例如 /orders/{id:int}。", page.RouteMessage);
        Assert.Equal(2, page.Routes.Count);

        Describe(page, "GET", "/status", ServerRouteKind.Status, string.Empty, status: 202);
        page.AddRouteCommand.Execute(null);
        ServerRouteSettings route = page.Routes[2];
        Assert.Equal(("GET", "/status", (int?)202, (string?)null), (route.Method, route.Template, route.StatusCode, route.Json));

        page.RemoveRouteCommand.Execute(route);
        Assert.Equal(2, page.Routes.Count);
    }

    [Fact]
    public async Task Routes_AreReplaced_WhenTheSameMethodAndTemplateIsRegisteredAgain()
    {
        await using var page = CreatePage();

        Describe(page, "GET", "/hello", ServerRouteKind.Fixed, "{\"message\":\"again\"}");
        page.AddRouteCommand.Execute(null);

        Assert.Equal(2, page.Routes.Count);
        Assert.Contains("again", page.Routes.Single(route => route.Template == "/hello").Json);
    }

    [Fact]
    public async Task Routes_CannotChange_WhileRunning()
    {
        await using var page = CreatePage();
        page.UrlsText = "http://127.0.0.1:0";
        await page.ConnectCommand.ExecuteAsync(null);
        Assert.Equal((SessionState.Running, false), (page.State, page.IsEditable));

        Describe(page, "GET", "/late", ServerRouteKind.Fixed, "{}");
        page.AddRouteCommand.Execute(null);
        page.RemoveRouteCommand.Execute(page.Routes[0]);

        Assert.Equal("运行中不能修改路由，请先停止。", page.RouteMessage);
        Assert.Equal(2, page.Routes.Count);
        await page.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Stopped, page.State);
    }

    [Fact]
    public async Task Start_ReportsTheBoundAddress_WhenThePortIsZero()
    {
        await using var page = CreatePage();
        page.UrlsText = "http://127.0.0.1:0";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.StartsWith("http://127.0.0.1:", page.BoundUrls);
        Assert.NotEqual("http://127.0.0.1:0", page.BoundUrls);
        await page.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, page.BoundUrls);
    }

    [Fact]
    public async Task Start_IsRefused_WhenTwoTemplatesAreTheSameRoute()
    {
        await using var page = CreatePage();
        // "/orders" 与 "/orders/" 不是同一个字符串，因此都能登记；库把它们当作同一个模板，映射时抛出 ArgumentException。
        Describe(page, "GET", "/orders", ServerRouteKind.Fixed, "[]");
        page.AddRouteCommand.Execute(null);
        Describe(page, "GET", "/orders/", ServerRouteKind.Fixed, "[]");
        page.AddRouteCommand.Execute(null);

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal((false, CommErrorKind.InvalidRequest), (record.Succeeded, record.ErrorKind));
    }

    [Fact]
    public async Task Start_IsRefused_WithoutAnyRoute()
    {
        await using var page = CreatePage();
        page.Routes.Clear();

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal("至少需要登记一条路由。", record.Message);
    }
}
