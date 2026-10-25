using System.IO;
using System.Text.Json;
using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.DebugAssistant.Tests.Workspace;

/// <summary>
/// REST 客户端、SOAP 客户端与 WebApi 服务端页的参数经工作区文件往返（计划 DA-D11）：多态类型标识 "restClient"、"soapClient"、
/// "webApiServer" 能读回同一个记录，路由表随文件保存，请求日志与 <c>KindText</c> 不写入文件。
/// </summary>
public sealed class WebApiSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-webapi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void RestSoapAndServerSettings_RoundTripThroughTheWorkspaceFile()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "workspace.json");
        RestClientSettings rest = RestSettings();
        SoapClientSettings soap = SoapSettings();
        WebApiServerSettings server = ServerSettings();
        new WorkspaceStore(path).Save(new WorkspaceDocument(
            WorkspaceDocument.CurrentSchemaVersion,
            [
                new WorkspaceSession("RestClient", "REST #1", rest),
                new WorkspaceSession("SoapClient", "SOAP #1", soap),
                new WorkspaceSession("HttpServer", "WebApi #1", server),
            ],
            SelectedIndex: 0));

        WorkspaceLoadResult loaded = new WorkspaceStore(path).Load();

        Assert.Null(loaded.Notice);
        Assert.Equal(Json(rest), Json(loaded.Document.Sessions[0].Settings!));
        Assert.Equal(Json(soap), Json(loaded.Document.Sessions[1].Settings!));
        Assert.Equal(Json(server), Json(loaded.Document.Sessions[2].Settings!));
        string text = File.ReadAllText(path);
        Assert.Contains("\"type\": \"restClient\"", text);
        Assert.Contains("\"type\": \"soapClient\"", text);
        Assert.Contains("\"type\": \"webApiServer\"", text);
        Assert.DoesNotContain("KindText", text);
    }

    private static RestClientSettings RestSettings() => new(
        "https://mes.example.com/api",
        5000,
        new WebApiAuthSettings(WebApiAuthScheme.Bearer, null, null, "MES_TOKEN", "X-API-Key", null),
        new WebApiRetrySettings(2, 500, false),
        new WebApiEnvelopeSettings(true, "code", "msg", "data", ["0"]),
        "health",
        30000,
        3,
        new WebApiLogSettings(false, ["Authorization"], ["token"]));

    private static SoapClientSettings SoapSettings() => new(
        "http://mes.example.com/service.asmx",
        SoapVersion.Soap12,
        "http://mes.example.com/ns",
        null,
        5000,
        new WebApiAuthSettings(WebApiAuthScheme.None, null, null, null, "X-API-Key", null),
        new WebApiRetrySettings(0, 1000, false),
        30000,
        3,
        new WebApiLogSettings(false, [], []));

    private static WebApiServerSettings ServerSettings() => new(
        ["http://127.0.0.1:0"],
        new WebApiAuthSettings(WebApiAuthScheme.ApiKey, null, null, null, "X-API-Key", "MES_KEY"),
        null,
        null,
        16,
        30000,
        1048576,
        5000,
        new WebApiEnvelopeSettings(false, "code", "msg", "data", ["0"]),
        200,
        new WebApiDeduplicationSettings(true, "Idempotency-Key", 600000, 1000),
        new WebApiLogSettings(false, ["Authorization"], ["token"]),
        [
            new ServerRouteSettings("GET", "/hello", ServerRouteKind.Fixed, "{\"message\":\"hello\"}", null, null, null, 0),
            new ServerRouteSettings("POST", "/orders", ServerRouteKind.BusinessFailure, null, "E100", "bad", null, 250),
        ]);
}
