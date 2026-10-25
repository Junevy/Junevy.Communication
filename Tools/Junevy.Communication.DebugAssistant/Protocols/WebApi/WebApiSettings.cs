using System.Text.Json.Serialization;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.DebugAssistant.Protocols.WebApi;

/// <summary>
/// 认证的参数（计划 7.9–7.11）：只保存环境变量的名称，值在创建客户端或服务器时读取，不写入工作区。
/// </summary>
public sealed record WebApiAuthSettings(
    WebApiAuthScheme Scheme,
    string? UserNameVariable,
    string? PasswordVariable,
    string? TokenVariable,
    string ApiKeyHeader,
    string? ApiKeyVariable);

/// <summary>业务信封的参数（计划 7.9）：是否启用、三个路径与成功码。</summary>
public sealed record WebApiEnvelopeSettings(
    bool Enabled,
    string CodePath,
    string MessagePath,
    string DataPath,
    string[] SuccessCodes);

/// <summary>重试的参数（计划 7.9）：次数、间隔（毫秒），以及是否重试不安全的方法。</summary>
public sealed record WebApiRetrySettings(int RetryCount, int RetryDelay, bool RetryUnsafeMethods);

/// <summary>日志的参数（计划 7.9）：是否记录正文，以及需要脱敏的请求头与查询参数。</summary>
public sealed record WebApiLogSettings(bool LogBodies, string[] RedactedHeaders, string[] RedactedQueryParameters);

/// <summary>REST 客户端页签的参数（计划第 7.9 节、DA-D11）。请求的内容（方法、路径、正文、幂等键）属于一次调用，不保存。</summary>
public sealed record RestClientSettings(
    string BaseUrl,
    int Timeout,
    WebApiAuthSettings Auth,
    WebApiRetrySettings Retry,
    WebApiEnvelopeSettings Envelope,
    string? HealthCheckPath,
    int HealthCheckInterval,
    int OfflineThreshold,
    WebApiLogSettings Logging)
    : SessionSettings;

/// <summary>SOAP 客户端页签的参数（计划第 7.10 节、DA-D11）。SOAPAction 与正文属于一次调用，不保存。</summary>
public sealed record SoapClientSettings(
    string Url,
    SoapVersion Version,
    string? DefaultNamespace,
    string? HealthCheckUrl,
    int Timeout,
    WebApiAuthSettings Auth,
    WebApiRetrySettings Retry,
    int HealthCheckInterval,
    int OfflineThreshold,
    WebApiLogSettings Logging)
    : SessionSettings;

/// <summary>
/// 服务端页签的参数（计划第 7.11 节、DA-D11）：监听地址、认证、并发与超时、业务信封、去重、HTTPS 与路由表。
/// 路由表是配置（运行中不能修改），因此随工作区保存；请求日志不保存。
/// </summary>
public sealed record WebApiServerSettings(
    string[] Urls,
    WebApiAuthSettings Auth,
    string? CertificatePath,
    string? CertificatePasswordVariable,
    int MaxConcurrentRequests,
    int RequestTimeout,
    long MaxRequestBodySize,
    int StopTimeout,
    WebApiEnvelopeSettings Envelope,
    int BusinessFailureStatusCode,
    WebApiDeduplicationSettings Deduplication,
    WebApiLogSettings Logging,
    ServerRouteSettings[] Routes)
    : SessionSettings;

/// <summary>去重的参数（计划 7.11）：是否启用、幂等键的请求头名称、窗口（毫秒）与缓存条数。</summary>
public sealed record WebApiDeduplicationSettings(bool Enabled, string HeaderName, int Window, int MaxEntries);

/// <summary>
/// 服务端的一条路由（计划 7.11）：方法、模板、响应方式、可选的延迟（用于演示 504）。
/// <see cref="ServerRouteKind.Fixed"/> 返回 <see cref="Json"/>；<see cref="ServerRouteKind.Status"/> 返回 <see cref="Json"/>（可为空）加指定的状态码；
/// <see cref="ServerRouteKind.Echo"/> 回显请求正文（GET 与 DELETE 回显方法、路径与查询参数）；<see cref="ServerRouteKind.BusinessFailure"/> 返回业务码与消息。
/// </summary>
public sealed record ServerRouteSettings(
    string Method,
    string Template,
    ServerRouteKind Kind,
    string? Json,
    string? Code,
    string? Message,
    int? StatusCode,
    int DelayMs)
{
    /// <summary>界面上显示的响应方式。</summary>
    [JsonIgnore]
    public string KindText => WebApiSupport.RouteKindText(Kind);
}

/// <summary>服务端路由的响应方式。</summary>
public enum ServerRouteKind
{
    /// <summary>固定的 JSON 正文。</summary>
    Fixed,

    /// <summary>指定状态码（正文可选）。</summary>
    Status,

    /// <summary>回显：请求正文（POST、PUT），或方法、路径与查询参数（GET、DELETE）。</summary>
    Echo,

    /// <summary>业务失败：业务码与消息。</summary>
    BusinessFailure,
}
