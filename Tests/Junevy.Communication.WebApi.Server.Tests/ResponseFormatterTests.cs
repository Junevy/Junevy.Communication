using System.Text.Json;
using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 响应格式（P32-D6）：包络 × 成功、业务失败、框架拒绝；非包络 × 成功、null 数据、业务失败、框架拒绝；显式状态码；嵌套路径；额外头。
/// </summary>
public sealed class ResponseFormatterTests
{
    private static ResponseFormatter FormatterWith(Action<WebApiServerConfig>? configure = null)
        => new ResponseFormatter(SettingsFactory.Create(configure));

    private static JsonDocument Json(ServerResponse response) => JsonDocument.Parse(response.Body!);

    [Fact]
    public void Envelope_Success_WritesCodeMsgAndDataAtTheConfiguredPaths()
    {
        ResponseFormatter formatter = FormatterWith(config => config.Envelope = SettingsFactory.NestedEnvelope());

        ServerResponse response = formatter.Format(WebApiServerResponse<Order>.Ok(new Order { Id = 42, Name = "widget" }));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        using JsonDocument document = Json(response);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.Equal("0", result.GetProperty("code").GetString());
        Assert.Equal(string.Empty, result.GetProperty("msg").GetString());
        Assert.Equal(42, result.GetProperty("data").GetProperty("id").GetInt32());
        Assert.Equal("widget", result.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public void Envelope_Success_WithNullData_WritesDataAsJsonNull()
    {
        ResponseFormatter formatter = FormatterWith(config => config.Envelope = SettingsFactory.NestedEnvelope());

        ServerResponse response = formatter.Format(WebApiServerResponse<Order>.Ok(null));

        Assert.Equal(200, response.StatusCode);
        using JsonDocument document = Json(response);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("result").GetProperty("data").ValueKind);
    }

    [Fact]
    public void Envelope_BusinessFailure_WritesCodeAndMsgWithoutData_AndUsesTheBusinessStatus()
    {
        ResponseFormatter formatter = FormatterWith(config => config.Envelope = SettingsFactory.NestedEnvelope());

        ServerResponse response = formatter.Format(WebApiServerResponse<Order>.Fail("E42", "rejected"));

        Assert.Equal(200, response.StatusCode);
        using JsonDocument document = Json(response);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.Equal("E42", result.GetProperty("code").GetString());
        Assert.Equal("rejected", result.GetProperty("msg").GetString());
        Assert.False(result.TryGetProperty("data", out _));
    }

    [Fact]
    public void Envelope_BusinessFailure_UsesTheConfiguredBusinessFailureStatus()
    {
        ResponseFormatter formatter = FormatterWith(config =>
        {
            config.Envelope = SettingsFactory.NestedEnvelope();
            config.BusinessFailureStatusCode = 409;
        });

        ServerResponse response = formatter.Format(WebApiServerResponse<Order>.Fail("E42", "conflict"));

        Assert.Equal(409, response.StatusCode);
    }

    [Fact]
    public void Envelope_ExplicitStatus_WinsOverTheConfiguredDefaults()
    {
        ResponseFormatter formatter = FormatterWith(config =>
        {
            config.Envelope = SettingsFactory.NestedEnvelope();
            config.BusinessFailureStatusCode = 409;
        });

        Assert.Equal(422, formatter.Format(WebApiServerResponse<Order>.Fail("E42", "x", 422)).StatusCode);
        Assert.Equal(201, formatter.Format(WebApiServerResponse<Order>.Ok(new Order(), 201)).StatusCode);
    }

    [Fact]
    public void Envelope_Rejection_WritesTheStatusTextAsCode()
    {
        ResponseFormatter formatter = FormatterWith(config => config.Envelope = SettingsFactory.NestedEnvelope());

        ServerResponse response = formatter.Reject(404, "404", "Not Found", new Dictionary<string, string>());

        Assert.Equal(404, response.StatusCode);
        using JsonDocument document = Json(response);
        Assert.Equal("404", document.RootElement.GetProperty("result").GetProperty("code").GetString());
        Assert.Equal("Not Found", document.RootElement.GetProperty("result").GetProperty("msg").GetString());
    }

    [Fact]
    public void Envelope_DeepPaths_BuildNestedObjectsThatShareTheirPrefixes()
    {
        ResponseFormatter formatter = FormatterWith(config => config.Envelope = new ResponseEnvelopeOptions
        {
            Enabled = true,
            CodePath = "a.b.code",
            MessagePath = "a.msg",
            DataPath = "data",
            SuccessCodes = new[] { "0" },
        });

        ServerResponse response = formatter.Format(WebApiServerResponse<Order>.Ok(new Order { Id = 7 }));

        using JsonDocument document = Json(response);
        Assert.Equal("0", document.RootElement.GetProperty("a").GetProperty("b").GetProperty("code").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("a").GetProperty("msg").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("data").GetProperty("id").GetInt32());
    }

    [Fact]
    public void Envelope_Success_UsesTheFirstSuccessCode()
    {
        ResponseFormatter formatter = FormatterWith(config => config.Envelope = new ResponseEnvelopeOptions
        {
            Enabled = true,
            SuccessCodes = new[] { "00", "0" },
        });

        using JsonDocument document = Json(formatter.Format(WebApiServerResponse<Order>.Ok(new Order())));

        Assert.Equal("00", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void NoEnvelope_Success_WritesTheDataJsonItself()
    {
        ResponseFormatter formatter = FormatterWith();

        ServerResponse response = formatter.Format(WebApiServerResponse<Order>.Ok(new Order { Id = 42, Name = "widget" }));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        using JsonDocument document = Json(response);
        Assert.Equal(42, document.RootElement.GetProperty("id").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("code", out _));
    }

    [Fact]
    public void NoEnvelope_NullData_IsNoContentWithoutBodyOrContentType()
    {
        ServerResponse response = FormatterWith().Format(WebApiServerResponse<Order>.Ok(null));

        Assert.Equal(204, response.StatusCode);
        Assert.Null(response.Body);
        Assert.Null(response.ContentType);
    }

    [Fact]
    public void NoEnvelope_ExplicitCreated_KeepsTheBody()
    {
        ServerResponse response = FormatterWith().Format(WebApiServerResponse<Order>.Ok(new Order { Id = 1 }, 201));

        Assert.Equal(201, response.StatusCode);
        Assert.NotNull(response.Body);
    }

    [Fact]
    public void NoEnvelope_ExplicitNoContent_DropsTheBody()
    {
        ServerResponse response = FormatterWith().Format(WebApiServerResponse<Order>.Ok(new Order { Id = 1 }, 204));

        Assert.Equal(204, response.StatusCode);
        Assert.Null(response.Body);
        Assert.Null(response.ContentType);
    }

    [Fact]
    public void NoEnvelope_BusinessFailure_IsProblemDetailsWithStatus400()
    {
        ServerResponse response = FormatterWith().Format(WebApiServerResponse<Order>.Fail("E42", "rejected"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        using JsonDocument document = Json(response);
        Assert.Equal("about:blank", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("Bad Request", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("rejected", document.RootElement.GetProperty("detail").GetString());
        Assert.Equal("E42", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void NoEnvelope_BusinessFailure_ExplicitStatus_IsUsed()
    {
        ServerResponse response = FormatterWith().Format(WebApiServerResponse<Order>.Fail("E42", "conflict", 409));

        Assert.Equal(409, response.StatusCode);
        using JsonDocument document = Json(response);
        Assert.Equal("Conflict", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void NoEnvelope_Rejection_UsesTheStatusAsTitleAndCode()
    {
        ServerResponse response = FormatterWith().Reject(405, "405", "Method Not Allowed", new Dictionary<string, string>());

        Assert.Equal(405, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        using JsonDocument document = Json(response);
        Assert.Equal("Method Not Allowed", document.RootElement.GetProperty("title").GetString());
        Assert.Equal("405", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Headers_AreCopiedAndMatchedCaseInsensitively_AndDoNotFollowLaterChanges()
    {
        var headers = new Dictionary<string, string> { ["Retry-After"] = "1" };

        ServerResponse response = FormatterWith().Reject(503, "503", "Service Unavailable", headers);
        headers["Retry-After"] = "changed";

        Assert.Equal("1", response.Headers["retry-after"]);
    }
}
