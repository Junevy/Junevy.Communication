using System.Xml.Linq;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// 把一次 SOAP 调用转成可重放的请求快照（内部，计划 Task 9 第 1 条）：校验 action 与可选项，写出信封，生成 <c>SOAPAction</c> 头与内容类型。
/// 参数非法时不抛出异常，而是返回英文原因，由外观交给 <c>Reject</c> 以 <c>InvalidRequest</c> 结束调用。SOAP 总是 POST，请求地址就是服务地址（相对 URI 为空）。
/// </summary>
internal sealed class SoapRequestBuilder
{
    private readonly EndpointSettings endpoint;
    private readonly SoapVersion version;

    /// <summary>创建构造器。</summary>
    /// <param name="endpoint">共有字段的配置快照（默认头与幂等头名）。</param>
    /// <param name="version">协议版本。</param>
    public SoapRequestBuilder(EndpointSettings endpoint, SoapVersion version)
    {
        this.endpoint = endpoint;
        this.version = version;
    }

    /// <summary>
    /// 构造请求快照。
    /// </summary>
    /// <param name="action">SOAPAction。</param>
    /// <param name="createBody">产生 Body 子元素的委托（强类型调用在这里序列化；它抛出的异常被转换为原因文本）。</param>
    /// <param name="options">本次调用的可选项；可为 null。</param>
    /// <param name="snapshot">成功时的快照；失败为 null。</param>
    /// <param name="error">失败时的英文原因；成功为 null。</param>
    /// <returns>是否成功。</returns>
    public bool TryBuild(string? action, Func<XElement?> createBody, SoapCallOptions? options, out HttpRequestSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = Validate(action, options);
        if (error != null)
            return false;

        if (!TryCreateBody(createBody, out XElement? body, out error))
            return false;

        byte[] envelope = SoapEnvelopeWriter.Write(version, body!, options?.SoapHeaders ?? Enumerable.Empty<XElement>());
        var headers = new Dictionary<string, string>(options?.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (version == SoapVersion.Soap11)
            headers[SoapProtocol.SoapActionHeader] = SoapProtocol.SoapActionHeaderValue(action!);

        snapshot = HttpRequestSnapshot.Create(endpoint, "POST", string.Empty, headers, SoapProtocol.ContentTypeOf(version, action!), envelope, options?.IdempotencyKey);
        return true;
    }

    // action 会被放进 HTTP 头：引号会破坏 SOAPAction 的引号，换行会造成头注入。
    private static string? Validate(string? action, SoapCallOptions? options)
    {
        if (action == null)
            return "The SOAP action must not be null.";
        if (action.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
            return "The SOAP action must not contain quotes or line breaks.";
        if (options?.SoapHeaders != null && options.SoapHeaders.Any(header => header == null))
            return "SoapHeaders must not contain null.";

        return EndpointArguments.TimeoutError(options?.Timeout);
    }

    private static bool TryCreateBody(Func<XElement?> createBody, out XElement? body, out string? error)
    {
        error = null;
        try
        {
            body = createBody();
        }
        catch (Exception ex)
        {
            // 边界：XmlSerializer 对它处理不了的类型抛出各种异常；转换为原因文本。
            body = null;
            error = $"The request body could not be serialized: {ex.Message}";
            return false;
        }

        if (body == null)
            error = "The request body must not be null.";

        return body != null;
    }
}
