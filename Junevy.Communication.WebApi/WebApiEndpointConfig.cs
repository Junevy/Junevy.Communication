namespace Junevy.Communication.WebApi;

/// <summary>
/// REST 与 SOAP 端点共有的配置（P3-D16）。构造客户端时校验并复制其值；客户端的运行行为只依赖构造时的快照，之后修改本对象不影响已创建的客户端。
/// 所有时间以毫秒为单位。
/// </summary>
public abstract class WebApiEndpointConfig
{
    /// <summary>每次尝试的超时（毫秒），覆盖拦截器、HTTP 收发与读完响应体；必须为正，默认 10000（P3-D11）。单次调用可以覆盖。</summary>
    public int Timeout { get; set; } = 10000;

    /// <summary>内置认证（Basic / Bearer / ApiKey）的配置，默认不认证。凭据只保存环境变量名，不保存值。</summary>
    public WebApiAuthOptions Auth { get; set; } = new WebApiAuthOptions();

    /// <summary>每个请求都带的头；头名不区分大小写，不能重复，请求自带的同名头优先。</summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = new Dictionary<string, string>();

    /// <summary>重试配置，默认不重试。</summary>
    public WebApiRetryOptions Retry { get; set; } = new WebApiRetryOptions();

    /// <summary>健康检查的间隔（毫秒），必须为正，默认 30000；只在配置了健康检查地址时生效。</summary>
    public int HealthCheckInterval { get; set; } = 30000;

    /// <summary>连续失败多少次后判定为离线，至少为 1，默认 3。</summary>
    public int OfflineThreshold { get; set; } = 3;

    /// <summary>日志选项（正文与脱敏）。</summary>
    public WebApiLogOptions Logging { get; set; } = new WebApiLogOptions();

    /// <summary>响应体的最大字节数，必须为正，默认 4194304（4 MiB）；超过即以协议违规失败。</summary>
    public int MaxResponseBodySize { get; set; } = 4194304;

    /// <summary>同一服务器的最大并发连接数，至少为 1，默认 10。</summary>
    public int MaxConnectionsPerServer { get; set; } = 10;

    /// <summary>携带幂等键的请求头名，默认 <c>Idempotency-Key</c>；null 或空字符串表示不发送。</summary>
    public string? IdempotencyHeaderName { get; set; } = "Idempotency-Key";
}
