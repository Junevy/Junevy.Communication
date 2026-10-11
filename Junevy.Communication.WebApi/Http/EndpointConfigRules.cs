namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 端点配置的校验规则（内部，P3-D16）：REST 与 SOAP 共有字段的校验只在这里写一次，通过后生成 <see cref="EndpointSettings"/> 快照。
/// 错误一律抛出 <see cref="ArgumentException"/> 族，参数名与消息都指出字段名（嵌套选项写成 <c>Retry.RetryCount</c>）。
/// 认证配置只校验结构（需要哪些变量名）；环境变量的读取与校验在创建认证拦截器时进行。
/// </summary>
internal static class EndpointConfigRules
{
    private const string AuthSection = nameof(WebApiEndpointConfig.Auth);
    private const string RetrySection = nameof(WebApiEndpointConfig.Retry);
    private const string LoggingSection = nameof(WebApiEndpointConfig.Logging);

    /// <summary>
    /// 校验配置并生成快照。校验顺序：标量字段 → 重试 → 日志 → 默认请求头 → 认证。
    /// </summary>
    /// <param name="config">端点配置；不能为 null，也不会被修改。</param>
    /// <returns>不可变快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个字段非法，或嵌套选项为 null。</exception>
    public static EndpointSettings CreateSettings(WebApiEndpointConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        RequireScalars(config);
        RequireRetry(config.Retry);
        RequireLogging(config.Logging);
        RequireHeaders(config);
        RequireAuth(config.Auth);

        return new EndpointSettings(config);
    }

    private static void RequireScalars(WebApiEndpointConfig config)
    {
        Positive(config.Timeout, nameof(WebApiEndpointConfig.Timeout));
        Positive(config.HealthCheckInterval, nameof(WebApiEndpointConfig.HealthCheckInterval));
        Positive(config.OfflineThreshold, nameof(WebApiEndpointConfig.OfflineThreshold));
        Positive(config.MaxResponseBodySize, nameof(WebApiEndpointConfig.MaxResponseBodySize));
        Positive(config.MaxConnectionsPerServer, nameof(WebApiEndpointConfig.MaxConnectionsPerServer));

        // null 或空字符串表示不发送幂等头；只有空白的名称一定是配置错误。
        if (!string.IsNullOrEmpty(config.IdempotencyHeaderName) && string.IsNullOrWhiteSpace(config.IdempotencyHeaderName))
            throw new ArgumentException("IdempotencyHeaderName must be null, empty, or a header name.", nameof(WebApiEndpointConfig.IdempotencyHeaderName));
    }

    private static void RequireRetry(WebApiRetryOptions retry)
    {
        RequireNotNull(retry, RetrySection);
        NonNegative(retry.RetryCount, Field(RetrySection, nameof(WebApiRetryOptions.RetryCount)));
        NonNegative(retry.RetryDelay, Field(RetrySection, nameof(WebApiRetryOptions.RetryDelay)));
    }

    private static void RequireLogging(WebApiLogOptions logging)
    {
        RequireNotNull(logging, LoggingSection);
        NonNegative(logging.MaxLoggedBodyLength, Field(LoggingSection, nameof(WebApiLogOptions.MaxLoggedBodyLength)));
        RequireNames(logging.RedactedHeaders, Field(LoggingSection, nameof(WebApiLogOptions.RedactedHeaders)));
        RequireNames(logging.RedactedQueryParameters, Field(LoggingSection, nameof(WebApiLogOptions.RedactedQueryParameters)));
    }

    // 头名不区分大小写，因此 X-Trace 与 x-trace 是同一个头；重复会让"哪个值生效"取决于字典的枚举顺序。
    private static void RequireHeaders(WebApiEndpointConfig config)
    {
        const string field = nameof(WebApiEndpointConfig.DefaultHeaders);
        RequireNotNull(config.DefaultHeaders, field);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in config.DefaultHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Key))
                throw new ArgumentException($"{field} must not contain an empty header name.", field);
            if (header.Value == null)
                throw new ArgumentException($"{field} header '{header.Key}' must not have a null value.", field);
            if (!seen.Add(header.Key))
                throw new ArgumentException($"{field} contains the header '{header.Key}' more than once (header names are case-insensitive).", field);
        }
    }

    private static void RequireAuth(WebApiAuthOptions auth)
    {
        RequireNotNull(auth, AuthSection);
        switch (auth.Scheme)
        {
            case WebApiAuthScheme.None:
                break;
            case WebApiAuthScheme.Basic:
                RequireAuthField(auth.UserNameVariable, nameof(WebApiAuthOptions.UserNameVariable), auth.Scheme);
                RequireAuthField(auth.PasswordVariable, nameof(WebApiAuthOptions.PasswordVariable), auth.Scheme);
                break;
            case WebApiAuthScheme.Bearer:
                RequireAuthField(auth.TokenVariable, nameof(WebApiAuthOptions.TokenVariable), auth.Scheme);
                break;
            case WebApiAuthScheme.ApiKey:
                RequireAuthField(auth.ApiKeyHeader, nameof(WebApiAuthOptions.ApiKeyHeader), auth.Scheme);
                RequireAuthField(auth.ApiKeyVariable, nameof(WebApiAuthOptions.ApiKeyVariable), auth.Scheme);
                break;
            default:
                throw new ArgumentOutOfRangeException(Field(AuthSection, nameof(WebApiAuthOptions.Scheme)), auth.Scheme, "Unknown authentication scheme.");
        }
    }

    private static void RequireAuthField(string? value, string property, WebApiAuthScheme scheme)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            string field = Field(AuthSection, property);
            throw new ArgumentException($"{field} is required when {Field(AuthSection, nameof(WebApiAuthOptions.Scheme))} is {scheme}.", field);
        }
    }

    private static void RequireNames(string[] names, string field)
    {
        RequireNotNull(names, field);
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"{field} must not contain an empty name.", field);
    }

    // 配置属性是非空类型，但调用方可能忽略可空性警告或用 null! 赋值，因此入口仍检查 null。
    private static void RequireNotNull(object? value, string field)
    {
        if (value == null)
            throw new ArgumentException($"{field} must not be null.", field);
    }

    private static string Field(string section, string property) => section + "." + property;

    private static void Positive(int value, string field)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(field, value, $"{field} must be positive.");
    }

    private static void NonNegative(int value, string field)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(field, value, $"{field} must not be negative.");
    }
}
