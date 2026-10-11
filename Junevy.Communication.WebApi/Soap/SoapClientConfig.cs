namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// SOAP 客户端的配置：在共有字段（<see cref="WebApiEndpointConfig"/>）之外增加服务地址、协议版本、默认命名空间与健康检查地址。
/// 构造 <see cref="SoapClient"/> 时校验并复制其值；之后修改本对象不影响已创建的客户端。
/// </summary>
public class SoapClientConfig : WebApiEndpointConfig
{
    /// <summary>服务的绝对 http / https 地址（可以带查询，不能带片段），例如 <c>http://mes.local/service.asmx</c>。</summary>
    public string Url { get; set; } = "";

    /// <summary>协议版本，默认 <see cref="SoapVersion.Soap11"/>。</summary>
    public SoapVersion Version { get; set; } = SoapVersion.Soap11;

    /// <summary>
    /// 强类型正文的默认 XML 命名空间：用于没有 <c>XmlRoot</c> / <c>XmlElement</c> 命名空间特性的类型。null 表示不设置。
    /// 原始 XML 版本（<c>InvokeRawAsync</c>）的元素按其自带的命名空间原样写出，不受它影响。
    /// </summary>
    public string? DefaultNamespace { get; set; }

    /// <summary>
    /// 健康检查地址，通常是 <c>...?wsdl</c>；null 或空表示不做健康检查。必须与 <see cref="Url"/> 同一个 scheme、主机与端口，
    /// 因为探测也会经过认证拦截器，不能把凭据带到别的主机。检查间隔见 <see cref="WebApiEndpointConfig.HealthCheckInterval"/>。
    /// </summary>
    public string? HealthCheckUrl { get; set; }
}
