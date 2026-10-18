using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 服务器配置的校验入口（内部）：逐项调用各条规则，通过后生成 <see cref="WebApiServerSettings"/> 快照。
/// 不修改配置对象与组件对象。错误一律抛出 <see cref="ArgumentException"/> 族，参数名指出字段名，消息英文。
/// </summary>
internal static class WebApiServerConfigRules
{
    /// <summary>
    /// 校验配置与组件并生成快照。
    /// </summary>
    /// <param name="config">服务器配置。</param>
    /// <param name="components">代码级组件。</param>
    /// <returns>不可变快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 或 <paramref name="components"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个字段非法。</exception>
    public static WebApiServerSettings Create(WebApiServerConfig? config, WebApiServerComponents? components)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(components);

        string[] urls = ServerUrlRules.Require(config.Urls);
        IWebApiRequestAuthenticator? authenticator = components.Authenticator;
        return new WebApiServerSettings
        {
            Urls = urls,
            Https = ServerUrlRules.HasHttps(urls) ? ServerHttpsRules.Create(config.Https, components.Certificate) : ServerHttpsSettings.NotRequired,
            Credentials = authenticator == null ? ServerCredentialRules.Create(config.Auth) : ServerCredentials.None,
            CustomAuthenticator = authenticator,
            Interceptors = RequireInterceptors(components.Interceptors),
            Serializer = RequireSerializer(components.Serializer),
            TimeProvider = components.TimeProvider ?? TimeProvider.System,
            MaxConcurrentRequests = RequirePositive(config.MaxConcurrentRequests, nameof(WebApiServerConfig.MaxConcurrentRequests)),
            RequestTimeout = RequirePositive(config.RequestTimeout, nameof(WebApiServerConfig.RequestTimeout)),
            MaxRequestBodySize = RequirePositive(config.MaxRequestBodySize, nameof(WebApiServerConfig.MaxRequestBodySize)),
            StopTimeout = RequirePositive(config.StopTimeout, nameof(WebApiServerConfig.StopTimeout)),
            BusinessFailureStatusCode = RequireBusinessFailureStatusCode(config.BusinessFailureStatusCode),
            Envelope = ServerEnvelopeRules.Create(config.Envelope),
            Deduplication = RequireDeduplication(config.Deduplication),
            Logging = RequireLogging(config.Logging),
        };
    }

    private static int RequirePositive(int value, string property)
    {
        if (value <= 0)
            throw new ArgumentException($"config.{property} must be greater than zero, not {value}.", "config." + property);

        return value;
    }

    private static long RequirePositive(long value, string property)
    {
        if (value <= 0)
            throw new ArgumentException($"config.{property} must be greater than zero, not {value}.", "config." + property);

        return value;
    }

    private static int RequireBusinessFailureStatusCode(int statusCode)
    {
        if (statusCode < 200 || statusCode > 599)
            throw new ArgumentException($"config.BusinessFailureStatusCode must be between 200 and 599, not {statusCode}.", "config.BusinessFailureStatusCode");

        return statusCode;
    }

    private static IWebApiServerInterceptor[] RequireInterceptors(IList<IWebApiServerInterceptor> interceptors)
    {
        if (interceptors.Any(interceptor => interceptor == null))
            throw new ArgumentException("components.Interceptors must not contain null.", "components.Interceptors");

        return interceptors.ToArray();
    }

    private static IWebApiSerializer RequireSerializer(IWebApiSerializer? serializer)
    {
        IWebApiSerializer result = serializer ?? new SystemTextJsonWebApiSerializer();
        if (!HttpSyntax.IsJsonMediaType(result.ContentType))
            throw new ArgumentException($"components.Serializer.ContentType must be a JSON media type ('application/json' or '*/*+json'), not '{result.ContentType}'.", "components.Serializer");

        return result;
    }

    private static WebApiServerDeduplicationOptions RequireDeduplication(WebApiServerDeduplicationOptions? options)
    {
        if (options == null)
            throw new ArgumentException("config.Deduplication must not be null.", "config.Deduplication");
        if (!HttpSyntax.IsToken(options.HeaderName))
            throw new ArgumentException($"config.Deduplication.HeaderName must be a valid HTTP header name, not '{options.HeaderName}'.", "config.Deduplication.HeaderName");

        return new WebApiServerDeduplicationOptions
        {
            Enabled = options.Enabled,
            HeaderName = options.HeaderName,
            Window = RequirePositive(options.Window, "Deduplication." + nameof(WebApiServerDeduplicationOptions.Window)),
            MaxEntries = RequirePositive(options.MaxEntries, "Deduplication." + nameof(WebApiServerDeduplicationOptions.MaxEntries)),
        };
    }

    private static WebApiLogOptions RequireLogging(WebApiLogOptions? logging)
    {
        if (logging == null)
            throw new ArgumentException("config.Logging must not be null.", "config.Logging");
        if (logging.MaxLoggedBodyLength < 0)
            throw new ArgumentException($"config.Logging.MaxLoggedBodyLength must not be negative, not {logging.MaxLoggedBodyLength}.", "config.Logging.MaxLoggedBodyLength");

        return new WebApiLogOptions
        {
            LogBodies = logging.LogBodies,
            MaxLoggedBodyLength = logging.MaxLoggedBodyLength,
            RedactedHeaders = CopyNames(logging.RedactedHeaders, "config.Logging.RedactedHeaders"),
            RedactedQueryParameters = CopyNames(logging.RedactedQueryParameters, "config.Logging.RedactedQueryParameters"),
        };
    }

    private static string[] CopyNames(string[]? names, string field)
    {
        if (names == null)
            throw new ArgumentException($"{field} must not be null.", field);
        if (names.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"{field} must not contain an empty name.", field);

        return (string[])names.Clone();
    }
}
