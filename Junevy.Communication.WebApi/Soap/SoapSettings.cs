using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// SOAP 客户端配置的不可变快照（内部）：共有字段的快照，加上服务地址、协议版本、默认命名空间与健康检查地址。
/// </summary>
internal sealed class SoapSettings
{
    /// <summary>创建快照。由 <see cref="SoapConfigRules"/> 在校验通过后调用。</summary>
    /// <param name="endpoint">共有字段的快照。</param>
    /// <param name="address">服务的绝对地址。</param>
    /// <param name="version">协议版本。</param>
    /// <param name="defaultNamespace">强类型正文的默认命名空间；没有为 null。</param>
    /// <param name="healthCheckUri">健康检查的绝对地址；不做健康检查为 null。</param>
    public SoapSettings(EndpointSettings endpoint, Uri address, SoapVersion version, string? defaultNamespace, Uri? healthCheckUri)
    {
        Endpoint = endpoint;
        Address = address;
        Version = version;
        DefaultNamespace = defaultNamespace;
        HealthCheckUri = healthCheckUri;
    }

    /// <summary>共有字段的快照。</summary>
    public EndpointSettings Endpoint { get; }

    /// <summary>服务的绝对地址。</summary>
    public Uri Address { get; }

    /// <summary>协议版本。</summary>
    public SoapVersion Version { get; }

    /// <summary>强类型正文的默认命名空间；没有为 null。</summary>
    public string? DefaultNamespace { get; }

    /// <summary>健康检查的绝对地址；不做健康检查为 null。</summary>
    public Uri? HealthCheckUri { get; }
}
