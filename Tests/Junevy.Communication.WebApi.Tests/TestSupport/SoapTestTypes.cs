using System.Xml.Serialization;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>SOAP 测试用的强类型请求（XmlSerializer 要求公开类型与公开的无参构造函数）。</summary>
[XmlRoot("GetOrder", Namespace = SoapTestNamespaces.Mes)]
public sealed class GetOrder
{
    [XmlElement(Namespace = SoapTestNamespaces.Mes)]
    public string? OrderNo { get; set; }
}

/// <summary>SOAP 测试用的强类型响应。</summary>
[XmlRoot("GetOrderResponse", Namespace = SoapTestNamespaces.Mes)]
public sealed class GetOrderResponse
{
    [XmlElement(Namespace = SoapTestNamespaces.Mes)]
    public string? Status { get; set; }

    [XmlElement(Namespace = SoapTestNamespaces.Mes)]
    public int Quantity { get; set; }
}

/// <summary>没有命名空间特性的请求类型：命名空间来自配置的 <c>DefaultNamespace</c>。</summary>
public sealed class Ping
{
    public int Seq { get; set; }
}

/// <summary>SOAP 测试用的命名空间常量。</summary>
public static class SoapTestNamespaces
{
    public const string Mes = "http://mes.local/ns";
    public const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
}
