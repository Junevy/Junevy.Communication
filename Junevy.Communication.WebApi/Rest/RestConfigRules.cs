using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// REST 配置的校验规则（内部，P3-D16）：共有字段交给 <see cref="EndpointConfigRules"/>，这里只校验 REST 独有的 BaseUrl、HealthCheckPath 与业务包络，
/// 通过后生成 <see cref="RestSettings"/> 快照。错误一律抛出 <see cref="ArgumentException"/> 族，参数名与消息都指出字段名。
/// </summary>
internal static class RestConfigRules
{
    private const string BaseUrlField = nameof(WebApiClientConfig.BaseUrl);
    private const string HealthCheckPathField = nameof(WebApiClientConfig.HealthCheckPath);
    private const string EnvelopeSection = nameof(WebApiClientConfig.Envelope);

    /// <summary>
    /// 校验配置并生成快照。校验顺序：共有字段 → BaseUrl → HealthCheckPath → 业务包络。
    /// </summary>
    /// <param name="config">REST 配置；不能为 null，也不会被修改。</param>
    /// <returns>不可变快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个字段非法。</exception>
    public static RestSettings Create(WebApiClientConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        EndpointSettings endpoint = EndpointConfigRules.CreateSettings(config);
        Uri baseAddress = RequireBaseAddress(config.BaseUrl);
        Uri? healthCheckUri = ResolveHealthCheck(config.HealthCheckPath, baseAddress);
        return new RestSettings(endpoint, baseAddress, healthCheckUri, RequireEnvelope(config.Envelope));
    }

    // 基地址规范化为以 / 结尾：之后任何相对路径都是"在基地址之下"，不会替换掉基地址自己的路径（http://host/api + orders = http://host/api/orders）。
    private static Uri RequireBaseAddress(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("BaseUrl must not be empty.", BaseUrlField);
        if (!HttpUrl.TryParseAbsolute(baseUrl, out Uri uri))
            throw new ArgumentException($"BaseUrl '{baseUrl}' must be an absolute http or https URL.", BaseUrlField);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException($"BaseUrl '{baseUrl}' must not contain a query or a fragment.", BaseUrlField);

        string path = uri.GetLeftPart(UriPartial.Path);
        return new Uri(path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/");
    }

    private static Uri? ResolveHealthCheck(string? healthCheckPath, Uri baseAddress)
    {
        if (string.IsNullOrWhiteSpace(healthCheckPath))
            return null;
        if (RestPaths.IsAbsoluteReference(healthCheckPath!))
            throw new ArgumentException($"HealthCheckPath '{healthCheckPath}' must be a relative path.", HealthCheckPathField);

        return new Uri(baseAddress, RestPaths.ToRelative(healthCheckPath!));
    }

    private static EnvelopeSettings RequireEnvelope(ResponseEnvelopeOptions envelope)
    {
        if (envelope == null)
            throw new ArgumentException("Envelope must not be null.", EnvelopeSection);

        // 未启用的包络不检查其余字段（与认证方式为 None 时一致）。
        if (envelope.Enabled)
        {
            RequirePath(envelope.CodePath, nameof(ResponseEnvelopeOptions.CodePath));
            RequirePath(envelope.MessagePath, nameof(ResponseEnvelopeOptions.MessagePath));
            RequirePath(envelope.DataPath, nameof(ResponseEnvelopeOptions.DataPath));
            RequireSuccessCodes(envelope.SuccessCodes);
        }

        return new EnvelopeSettings(envelope);
    }

    private static void RequirePath(string? path, string property)
    {
        if (!EnvelopeSettings.TryParsePath(path, out _))
        {
            string field = EnvelopeSection + "." + property;
            throw new ArgumentException($"{field} must be a dotted path without empty segments, not '{path}'.", field);
        }
    }

    private static void RequireSuccessCodes(string[]? successCodes)
    {
        const string field = EnvelopeSection + "." + nameof(ResponseEnvelopeOptions.SuccessCodes);
        if (successCodes == null || successCodes.Length == 0)
            throw new ArgumentException($"{field} must contain at least one code.", field);
        if (successCodes.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"{field} must not contain an empty code.", field);
    }
}
