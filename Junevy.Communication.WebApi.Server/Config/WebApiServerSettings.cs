using Junevy.Communication.WebApi;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 服务器配置的不可变快照（内部）：由 <see cref="WebApiServerConfigRules"/> 校验后创建，之后不再读取调用方的配置与组件对象。
/// </summary>
internal sealed class WebApiServerSettings
{
    /// <summary>监听地址（副本）。</summary>
    public required string[] Urls { get; init; }

    /// <summary>HTTPS 证书来源；没有 https 地址时为 <see cref="ServerHttpsSettings.NotRequired"/>。</summary>
    public required ServerHttpsSettings Https { get; init; }

    /// <summary>内置认证的凭据；设置了自定义认证器，或认证方式为 None 时为 <see cref="ServerCredentials.None"/>。</summary>
    public required ServerCredentials Credentials { get; init; }

    /// <summary>自定义认证器；没有时为 null。</summary>
    public required IWebApiRequestAuthenticator? CustomAuthenticator { get; init; }

    /// <summary>拦截器（副本，按注册顺序）。</summary>
    public required IWebApiServerInterceptor[] Interceptors { get; init; }

    /// <summary>JSON 序列化器。</summary>
    public required IWebApiSerializer Serializer { get; init; }

    /// <summary>时间源（去重窗口用）。</summary>
    public required TimeProvider TimeProvider { get; init; }

    /// <summary>同时处理的请求上限。</summary>
    public required int MaxConcurrentRequests { get; init; }

    /// <summary>单个请求的超时（毫秒）。</summary>
    public required int RequestTimeout { get; init; }

    /// <summary>请求正文的上限（字节）。</summary>
    public required long MaxRequestBodySize { get; init; }

    /// <summary>停止时等待在途请求的最长时间（毫秒）。</summary>
    public required int StopTimeout { get; init; }

    /// <summary>业务失败的默认 HTTP 状态码（仅在启用包络时使用）。</summary>
    public required int BusinessFailureStatusCode { get; init; }

    /// <summary>业务包络的字段路径与成功码。</summary>
    public required ServerEnvelopeSettings Envelope { get; init; }

    /// <summary>去重配置（副本）。</summary>
    public required WebApiServerDeduplicationOptions Deduplication { get; init; }

    /// <summary>日志选项（副本）。</summary>
    public required WebApiLogOptions Logging { get; init; }
}
