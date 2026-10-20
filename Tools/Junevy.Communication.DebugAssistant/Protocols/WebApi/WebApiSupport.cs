using System.Globalization;
using System.Text.Json;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Server;

namespace Junevy.Communication.DebugAssistant.Protocols.WebApi;

/// <summary>
/// WebApi 三个页的纯逻辑：参数到库选项的映射、路由与 JSON 的校验、显示文字，不依赖界面，便于单元测试。
/// </summary>
public static class WebApiSupport
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE"];

    /// <summary>服务端路由的方法（界面的下拉选项）。</summary>
    public static IReadOnlyList<string> MethodChoices { get; } = Methods;

    public static WebApiAuthOptions ToAuthOptions(WebApiAuthSettings settings) => new()
    {
        Scheme = settings.Scheme,
        UserNameVariable = Optional(settings.UserNameVariable),
        PasswordVariable = Optional(settings.PasswordVariable),
        TokenVariable = Optional(settings.TokenVariable),
        ApiKeyHeader = settings.ApiKeyHeader,
        ApiKeyVariable = Optional(settings.ApiKeyVariable),
    };

    public static ResponseEnvelopeOptions ToEnvelopeOptions(WebApiEnvelopeSettings settings) => new()
    {
        Enabled = settings.Enabled,
        CodePath = settings.CodePath,
        MessagePath = settings.MessagePath,
        DataPath = settings.DataPath,
        SuccessCodes = settings.SuccessCodes,
    };

    public static WebApiRetryOptions ToRetryOptions(WebApiRetrySettings settings) => new()
    {
        RetryCount = settings.RetryCount,
        RetryDelay = settings.RetryDelay,
        RetryUnsafeMethods = settings.RetryUnsafeMethods,
    };

    public static WebApiLogOptions ToLogOptions(WebApiLogSettings settings) => new()
    {
        LogBodies = settings.LogBodies,
        RedactedHeaders = settings.RedactedHeaders,
        RedactedQueryParameters = settings.RedactedQueryParameters,
    };

    /// <summary>可用性的中文名称。</summary>
    public static string AvailabilityText(WebApiAvailability availability) => availability switch
    {
        WebApiAvailability.Online => "在线",
        WebApiAvailability.Offline => "离线",
        _ => "未知",
    };

    /// <summary>服务端一次请求的结果分类的中文名称。</summary>
    public static string OutcomeText(WebApiServerOutcome outcome) => outcome switch
    {
        WebApiServerOutcome.Succeeded => "成功",
        WebApiServerOutcome.BusinessFailure => "业务失败",
        WebApiServerOutcome.Rejected => "拒绝",
        WebApiServerOutcome.HandlerFailed => "处理器异常",
        WebApiServerOutcome.TimedOut => "超时",
        WebApiServerOutcome.Replayed => "去重重放",
        WebApiServerOutcome.ClientDisconnected => "客户端断开",
        _ => outcome.ToString(),
    };

    /// <summary>服务端状态的中文名称。</summary>
    public static string ServerStateText(WebApiServerState state) => state switch
    {
        WebApiServerState.Stopped => "已停止",
        WebApiServerState.Starting => "启动中",
        WebApiServerState.Running => "运行中",
        WebApiServerState.Stopping => "停止中",
        _ => state.ToString(),
    };

    /// <summary>路由响应方式的中文名称。</summary>
    public static string RouteKindText(ServerRouteKind kind) => kind switch
    {
        ServerRouteKind.Fixed => "固定 JSON",
        ServerRouteKind.Status => "指定状态码",
        ServerRouteKind.Echo => "回显",
        ServerRouteKind.BusinessFailure => "业务失败",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 路由的校验：方法、模板、JSON 正文与状态码。返回的是中文说明；合法时为 null。
    /// 库只在启动时校验模板（重复或无法映射），这里在登记时就给出原因。
    /// </summary>
    public static string? RouteProblem(ServerRouteSettings route)
    {
        if (!Methods.Contains(route.Method))
        {
            return "方法只能是 GET、POST、PUT 或 DELETE。";
        }

        if (string.IsNullOrWhiteSpace(route.Template) || !route.Template.StartsWith('/'))
        {
            return "路由模板必须以 / 开头，例如 /orders/{id:int}。";
        }

        if (route.DelayMs < 0)
        {
            return "延迟不能为负数。";
        }

        switch (route.Kind)
        {
            case ServerRouteKind.Fixed:
                if (string.IsNullOrWhiteSpace(route.Json))
                {
                    return "固定 JSON 的响应需要填写 JSON 正文。";
                }

                return JsonProblem(route.Json) ?? StatusProblem(route.StatusCode, 200, 299);
            case ServerRouteKind.Status:
                if (route.StatusCode is null)
                {
                    return "指定状态码的响应需要填写状态码。";
                }

                return (string.IsNullOrWhiteSpace(route.Json) ? null : JsonProblem(route.Json)) ?? StatusProblem(route.StatusCode, 200, 299);
            case ServerRouteKind.Echo:
                return StatusProblem(route.StatusCode, 200, 299);
            case ServerRouteKind.BusinessFailure:
                if (string.IsNullOrWhiteSpace(route.Code))
                {
                    return "业务失败需要填写业务码。";
                }

                if (string.IsNullOrWhiteSpace(route.Message))
                {
                    return "业务失败需要填写消息。";
                }

                return StatusProblem(route.StatusCode, 200, 599);
            default:
                return "未知的响应方式。";
        }
    }

    /// <summary>服务端参数的校验：至少一个监听地址，至少一条路由。</summary>
    public static string? ServerProblem(WebApiServerSettings settings)
    {
        if (settings.Urls.Length == 0)
        {
            return "至少需要一个监听地址。";
        }

        return settings.Routes.Length == 0 ? "至少需要登记一条路由。" : null;
    }

    /// <summary>JSON 文本的校验；合法时为 null。</summary>
    public static string? JsonProblem(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return null;
        }
        catch (JsonException ex)
        {
            return $"JSON 无效：{ex.Message}";
        }
    }

    /// <summary>把 JSON 文本解析为 <see cref="JsonElement"/>（调用前应先通过 <see cref="JsonProblem"/>）。</summary>
    public static JsonElement ParseJson(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>用于显示的 JSON 缩进文本；不是 JSON 时原样返回。</summary>
    public static string Pretty(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>按行拆分，去掉空行与首尾空白。</summary>
    public static string[] ParseLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>按逗号或换行拆分，去掉空项与首尾空白。</summary>
    public static string[] ParseList(string text) =>
        text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string JoinLines(IEnumerable<string> items) => string.Join(Environment.NewLine, items);

    public static string JoinList(IEnumerable<string> items) => string.Join(", ", items);

    /// <summary>可选的文本：空白表示没有。</summary>
    public static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>状态码的显示文字；网络失败等没有状态码时为 "—"。</summary>
    public static string StatusText(int? status) => status?.ToString(CultureInfo.InvariantCulture) ?? "—";

    /// <summary>时间线的一行。尝试编号为 0（与尝试无关的事件）时不显示。</summary>
    public static WebApiTimelineRow TimelineRow(DateTimeOffset now, string eventName, int attempt, string status, string detail) =>
        new(
            now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            eventName,
            attempt == 0 ? string.Empty : attempt.ToString(CultureInfo.InvariantCulture),
            status,
            detail);

    /// <summary>响应事件的说明：将要重试时写出错误种类与延迟，否则写出错误种类（成功时为空）。</summary>
    public static string ResponseDetail(WebApiResponseEventArgs e) =>
        e.WillRetry
            ? $"{CommErrorKindNames.Of(e.ErrorKind)}，{e.RetryDelay.ToString(CultureInfo.InvariantCulture)} 毫秒后重试"
            : e.ErrorKind == CommErrorKind.None ? string.Empty : CommErrorKindNames.Of(e.ErrorKind);

    /// <summary>完成事件的说明：成功或失败的原因，加耗时（毫秒）。</summary>
    public static string CompletedDetail(WebApiCompletedEventArgs e)
    {
        string outcome = e.IsSuccess ? "成功" : ResultText.Failure(e.ErrorKind, e.ErrorMessage);
        return $"{outcome}（{e.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} 毫秒）";
    }

    private static string? StatusProblem(int? status, int min, int max) =>
        status is { } value && (value < min || value > max)
            ? $"状态码必须在 {min} 到 {max} 之间。"
            : null;
}
