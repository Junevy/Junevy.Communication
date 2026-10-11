namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// 一次 SOAP 调用的可选项。参数非法（Timeout 不是正数、SoapHeaders 含 null）不会抛出异常，调用以 <c>InvalidRequest</c> 失败。
/// </summary>
public sealed class SoapCallOptions
{
    /// <summary>覆盖配置里的每次尝试超时（毫秒）；必须为正，null 表示使用配置值。</summary>
    public int? Timeout { get; set; }

    /// <summary>本次调用的 HTTP 头，覆盖配置里同名的 <see cref="WebApiEndpointConfig.DefaultHeaders"/>；<c>SOAPAction</c> 由库按 action 生成，不能覆盖。</summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>写入 <c>soap:Header</c> 的元素，按顺序写出（WS-Security 等扩展用它或拦截器实现）。写出的是副本，不会改动这里的元素。</summary>
    public IList<System.Xml.Linq.XElement>? SoapHeaders { get; set; }

    /// <summary>幂等键：设置后作为请求头发给对端，并允许重试（SOAP 总是 POST，默认不重试）。SOAP Fault 永远不会被重试。</summary>
    public string? IdempotencyKey { get; set; }
}
