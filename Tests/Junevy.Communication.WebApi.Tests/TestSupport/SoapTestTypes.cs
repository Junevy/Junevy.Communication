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

/// <summary>
/// 带 <c>object</c> 成员和可空成员的类型：<c>object</c> 成员序列化时会写 <c>xsi:type="xsd:int"</c> 这样的 QName 值属性，
/// 可空成员为 null 时会写 <c>xsi:nil</c>，它们依赖对应的命名空间前缀声明仍然有效。
/// </summary>
[XmlRoot("Variant", Namespace = SoapTestNamespaces.Mes)]
public sealed class VariantBody
{
    [XmlElement(Namespace = SoapTestNamespaces.Mes)]
    public object? Value { get; set; }

    [XmlElement(Namespace = SoapTestNamespaces.Mes, IsNullable = true)]
    public string? Note { get; set; }

    [XmlElement(Namespace = SoapTestNamespaces.Mes, IsNullable = true)]
    public int? Count { get; set; }
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
