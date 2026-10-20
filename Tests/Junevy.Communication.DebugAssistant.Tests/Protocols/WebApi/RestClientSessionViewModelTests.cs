using System.Text.Json;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.WebApi;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.WebApi;

/// <summary>
/// REST 客户端页的视图模型，不发起网络请求（计划 Task 18 第 2 条的单元部分）：默认值、设置往返（数组字段按 JSON 比较）、
/// 基地址为空时拒绝并记录、未连接时不能发送、查询参数与请求头的增删。与服务端页的端到端行为见 <c>WebApiEndToEndTests</c>。
/// </summary>
public sealed class RestClientSessionViewModelTests
{
    private static RestClientSessionViewModel CreatePage() =>
        new(ProtocolCatalog.Get(ProtocolKind.RestClient), FakeServices.Create(new ManualUiDispatcher()));

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task Page_StartsNotConnected_WithTheDefaults()
    {
        await using var page = CreatePage();

        Assert.Equal((SessionState.NotConnected, true, false, "未知"), (page.State, page.IsEditable, page.IsLinked, page.AvailabilityText));
        Assert.Equal((WebApiAuthScheme.None, "POST", "reports"), (page.AuthScheme, page.Method, page.Path));
        Assert.False(page.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Settings_RoundTrip_KeepEveryField()
    {
        await using var page = CreatePage();
        page.BaseUrl = "https://mes.example.com/api";
        page.TimeoutMs = 7000;
        page.AuthScheme = WebApiAuthScheme.Bearer;
        page.TokenVariable = "MES_TOKEN";
        page.RetryCount = 2;
        page.RetryDelayMs = 250;
        page.RetryUnsafeMethods = true;
        page.EnvelopeEnabled = true;
        page.SuccessCodesText = "0, OK";
        page.HealthCheckPath = "health";
        page.HealthCheckIntervalMs = 2000;
        page.OfflineThreshold = 2;
        page.LogBodies = true;
        page.RedactedHeadersText = "Authorization, X-Secret";

        var saved = (RestClientSettings)page.CaptureSettings();
        await using var other = CreatePage();
        other.ApplySettings(saved);

        Assert.Equal(Json(saved), Json(other.CaptureSettings()));
        Assert.Equal(("MES_TOKEN", "0, OK"), (other.TokenVariable, other.SuccessCodesText));
    }

    [Fact]
    public async Task ConnectWithAnEmptyBaseUrl_IsRefused_AndRecorded()
    {
        await using var page = CreatePage();
        page.BaseUrl = string.Empty;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal((false, CommErrorKind.InvalidRequest), (record.Succeeded, record.ErrorKind));
    }

    [Fact]
    public async Task Sending_IsAvailable_OnlyOnceConnected()
    {
        await using var page = CreatePage();
        Assert.False(page.SendCommand.CanExecute(null));

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal((SessionState.Connected, true, true), (page.State, page.IsLinked, page.SendCommand.CanExecute(null)));
    }

    [Fact]
    public async Task QueryAndHeaderRows_AddAndRemove_AndRefuseAnEmptyName()
    {
        await using var page = CreatePage();

        page.QueryName = " ";
        page.AddQueryCommand.Execute(null);
        Assert.Equal("查询参数名不能为空。", page.RequestMessage);
        Assert.Empty(page.QueryRows);

        page.QueryName = "lot";
        page.QueryValue = "A-1";
        page.AddQueryCommand.Execute(null);
        Assert.Equal(new WebApiNameValueRow("lot", "A-1"), Assert.Single(page.QueryRows));
        Assert.Equal((string.Empty, string.Empty), (page.QueryName, page.QueryValue));
        page.RemoveQueryCommand.Execute(page.QueryRows[0]);
        Assert.Empty(page.QueryRows);

        page.HeaderName = "X-Trace";
        page.HeaderValue = "t-1";
        page.AddHeaderCommand.Execute(null);
        Assert.Equal(new WebApiNameValueRow("X-Trace", "t-1"), Assert.Single(page.HeaderRows));
        page.RemoveHeaderCommand.Execute(page.HeaderRows[0]);
        Assert.Empty(page.HeaderRows);
    }
}
