namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 端点配置的不可变快照（P3-D16）。构造客户端时由 <see cref="EndpointConfigRules.CreateSettings"/> 校验并生成，
/// 之后端点只读取快照，调用方再修改配置对象不会生效。集合与嵌套选项都是副本。
/// </summary>
internal sealed class EndpointSettings
{
    /// <summary>
    /// 复制已校验的配置。只应由 <see cref="EndpointConfigRules"/> 在校验通过后调用。
    /// </summary>
    /// <param name="config">已通过校验的配置。</param>
    internal EndpointSettings(WebApiEndpointConfig config)
    {
        Timeout = config.Timeout;
        HealthCheckInterval = config.HealthCheckInterval;
        OfflineThreshold = config.OfflineThreshold;
        MaxResponseBodySize = config.MaxResponseBodySize;
        MaxConnectionsPerServer = config.MaxConnectionsPerServer;
        IdempotencyHeaderName = string.IsNullOrEmpty(config.IdempotencyHeaderName) ? null : config.IdempotencyHeaderName;
        DefaultHeaders = new Dictionary<string, string>(config.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        Auth = CopyAuth(config.Auth);
        RetryCount = config.Retry.RetryCount;
        RetryDelay = config.Retry.RetryDelay;
        RetryUnsafeMethods = config.Retry.RetryUnsafeMethods;
        LogBodies = config.Logging.LogBodies;
        MaxLoggedBodyLength = config.Logging.MaxLoggedBodyLength;
        RedactedHeaders = (string[])config.Logging.RedactedHeaders.Clone();
        RedactedQueryParameters = (string[])config.Logging.RedactedQueryParameters.Clone();
    }

    /// <summary>每次尝试的超时（毫秒）。</summary>
    public int Timeout { get; }

    /// <summary>健康检查的间隔（毫秒）。</summary>
    public int HealthCheckInterval { get; }

    /// <summary>判定离线所需的连续失败次数。</summary>
    public int OfflineThreshold { get; }

    /// <summary>响应体的最大字节数。</summary>
    public int MaxResponseBodySize { get; }

    /// <summary>同一服务器的最大并发连接数。</summary>
    public int MaxConnectionsPerServer { get; }

    /// <summary>携带幂等键的请求头名；null 表示不发送。</summary>
    public string? IdempotencyHeaderName { get; }

    /// <summary>每个请求都带的头（头名不区分大小写）。</summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders { get; }

    /// <summary>内置认证的配置副本；环境变量由 Auth 命名空间在创建拦截器时读取。</summary>
    public WebApiAuthOptions Auth { get; }

    /// <summary>首次尝试失败后最多再试几次。</summary>
    public int RetryCount { get; }

    /// <summary>两次尝试之间的固定间隔（毫秒）。</summary>
    public int RetryDelay { get; }

    /// <summary>是否允许重试没有幂等键的非幂等方法。</summary>
    public bool RetryUnsafeMethods { get; }

    /// <summary>是否记录正文。</summary>
    public bool LogBodies { get; }

    /// <summary>记录正文时最多输出的字符数。</summary>
    public int MaxLoggedBodyLength { get; }

    /// <summary>需要脱敏的请求头名（比较时不区分大小写）。</summary>
    public IReadOnlyList<string> RedactedHeaders { get; }

    /// <summary>需要脱敏的查询参数名（比较时不区分大小写）。</summary>
    public IReadOnlyList<string> RedactedQueryParameters { get; }

    private static WebApiAuthOptions CopyAuth(WebApiAuthOptions source)
        => new WebApiAuthOptions
        {
            Scheme = source.Scheme,
            UserNameVariable = source.UserNameVariable,
            PasswordVariable = source.PasswordVariable,
            TokenVariable = source.TokenVariable,
            ApiKeyHeader = source.ApiKeyHeader,
            ApiKeyVariable = source.ApiKeyVariable,
        };
}
