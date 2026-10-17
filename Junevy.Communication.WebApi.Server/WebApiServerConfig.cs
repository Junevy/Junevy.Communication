using Junevy.Communication.WebApi;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// HTTP 服务端的配置（P32-D3 至 D13）。创建服务器时校验并拍快照，之后修改本对象不影响已经创建的服务器。
/// 凭据与证书口令只保存环境变量名，值在创建服务器时读取。
/// </summary>
public sealed class WebApiServerConfig
{
    /// <summary>
    /// 监听地址，使用 Kestrel 的写法（<c>http://*:8080</c>、<c>http://localhost:8080</c>、<c>https://0.0.0.0:8443</c>），必须写端口；
    /// 端口可为 0，由系统分配，启动后以实际绑定的地址为准。默认 <c>http://*:8080</c>。
    /// </summary>
    public string[] Urls { get; set; } = { "http://*:8080" };

    /// <summary>HTTPS 证书的配置；只有配置了 https 地址时才需要。</summary>
    public WebApiServerHttpsOptions Https { get; set; } = new();

    /// <summary>内置认证的配置（默认不认证）；设置了 <see cref="WebApiServerComponents.Authenticator"/> 时被取代。</summary>
    public WebApiAuthOptions Auth { get; set; } = new();

    /// <summary>同时处理的请求上限，超出时立即返回 503 且不排队。默认 16。</summary>
    public int MaxConcurrentRequests { get; set; } = 16;

    /// <summary>单个请求的超时（毫秒），覆盖读取正文、拦截器与处理器。默认 30000。</summary>
    public int RequestTimeout { get; set; } = 30000;

    /// <summary>请求正文的上限（字节），超出时返回 413。默认 1 MiB。</summary>
    public long MaxRequestBodySize { get; set; } = 1048576;

    /// <summary>停止时等待在途请求的最长时间（毫秒），之后中止剩余连接。默认 5000。</summary>
    public int StopTimeout { get; set; } = 5000;

    /// <summary>业务包络的配置；启用后成功与业务失败都写成 <c>{ code, msg, data }</c>。</summary>
    public ResponseEnvelopeOptions Envelope { get; set; } = new();

    /// <summary>启用业务包络时，业务失败的 HTTP 状态码（200–599）；处理器显式指定的状态码优先。默认 200。</summary>
    public int BusinessFailureStatusCode { get; set; } = 200;

    /// <summary>重复下发去重的配置（默认关闭）。</summary>
    public WebApiServerDeduplicationOptions Deduplication { get; set; } = new();

    /// <summary>日志选项：是否记录正文，以及需要脱敏的请求头与查询参数。</summary>
    public WebApiLogOptions Logging { get; set; } = new();
}
