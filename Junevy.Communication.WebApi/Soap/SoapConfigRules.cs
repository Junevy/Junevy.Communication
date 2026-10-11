using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// SOAP 配置的校验规则（内部，P3-D16）：共有字段交给 <see cref="EndpointConfigRules"/>，这里只校验 SOAP 独有的 Url、Version、DefaultNamespace 与 HealthCheckUrl，
/// 通过后生成 <see cref="SoapSettings"/> 快照。错误一律抛出 <see cref="ArgumentException"/> 族，参数名与消息都指出字段名。
/// </summary>
internal static class SoapConfigRules
{
    private const string UrlField = nameof(SoapClientConfig.Url);
    private const string HealthCheckUrlField = nameof(SoapClientConfig.HealthCheckUrl);

    /// <summary>
    /// 校验配置并生成快照。校验顺序：共有字段 → Url → Version → DefaultNamespace → HealthCheckUrl。
    /// </summary>
    /// <param name="config">SOAP 配置；不能为 null，也不会被修改。</param>
    /// <returns>不可变快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个字段非法。</exception>
    public static SoapSettings Create(SoapClientConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        EndpointSettings endpoint = EndpointConfigRules.CreateSettings(config);
        Uri address = RequireUrl(config.Url);
        if (!Enum.IsDefined(typeof(SoapVersion), config.Version))
            throw new ArgumentOutOfRangeException(nameof(SoapClientConfig.Version), config.Version, "Unknown SOAP version.");
        if (config.DefaultNamespace != null && string.IsNullOrWhiteSpace(config.DefaultNamespace))
            throw new ArgumentException("DefaultNamespace must be null or a namespace.", nameof(SoapClientConfig.DefaultNamespace));

        return new SoapSettings(endpoint, address, config.Version, config.DefaultNamespace, ResolveHealthCheck(config.HealthCheckUrl, address));
    }

    private static Uri RequireUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Url must not be empty.", UrlField);
        if (!HttpUrl.TryParseAbsolute(url, out Uri address))
            throw new ArgumentException($"Url '{url}' must be an absolute http or https URL.", UrlField);
        if (address.Fragment.Length > 0)
            throw new ArgumentException($"Url '{url}' must not contain a fragment.", UrlField);

        return address;
    }

    // 健康检查也会经过认证拦截器，因此必须与服务地址同源，凭据不能被带到别的主机。
    private static Uri? ResolveHealthCheck(string? healthCheckUrl, Uri address)
    {
        if (string.IsNullOrWhiteSpace(healthCheckUrl))
            return null;
        if (!HttpUrl.TryParseAbsolute(healthCheckUrl, out Uri healthUri))
            throw new ArgumentException($"HealthCheckUrl '{healthCheckUrl}' must be an absolute http or https URL.", HealthCheckUrlField);
        if (!UriOrigin.AreSame(address, healthUri))
            throw new ArgumentException($"HealthCheckUrl '{healthCheckUrl}' must have the same scheme, host and port as Url.", HealthCheckUrlField);

        return healthUri;
    }
}
