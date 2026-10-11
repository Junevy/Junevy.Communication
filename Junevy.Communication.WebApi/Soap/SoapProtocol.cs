using System.Xml.Linq;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// SOAP 1.1 / 1.2 的协议常量（内部）：信封命名空间，以及请求的内容类型与 <c>SOAPAction</c> 头。请求的写出与响应的读取共用，避免两处各写一份。
/// </summary>
internal static class SoapProtocol
{
    /// <summary>SOAP 1.1 信封命名空间。</summary>
    public static readonly XNamespace Soap11Namespace = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>SOAP 1.2 信封命名空间。</summary>
    public static readonly XNamespace Soap12Namespace = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>SOAP 1.1 请求里 action 所在的 HTTP 头。</summary>
    public const string SoapActionHeader = "SOAPAction";

    private const string Soap11ContentType = "text/xml; charset=utf-8";
    private const string Soap12ContentType = "application/soap+xml; charset=utf-8";

    /// <summary>某个版本的信封命名空间。</summary>
    /// <param name="version">协议版本。</param>
    /// <returns>命名空间。</returns>
    public static XNamespace NamespaceOf(SoapVersion version) => version == SoapVersion.Soap12 ? Soap12Namespace : Soap11Namespace;

    /// <summary>
    /// 请求的内容类型。1.1 固定为 <c>text/xml; charset=utf-8</c>；1.2 把 action 放进参数：<c>application/soap+xml; charset=utf-8; action="..."</c>（action 为空串时省略该参数）。
    /// </summary>
    /// <param name="version">协议版本。</param>
    /// <param name="action">action（已校验不含引号与换行）。</param>
    /// <returns>内容类型。</returns>
    public static string ContentTypeOf(SoapVersion version, string action)
    {
        if (version == SoapVersion.Soap11)
            return Soap11ContentType;

        return action.Length == 0 ? Soap12ContentType : $"{Soap12ContentType}; action=\"{action}\"";
    }

    /// <summary>
    /// 1.1 的 <c>SOAPAction</c> 头值：总是带引号，action 为空串时是 <c>""</c>。
    /// </summary>
    /// <param name="action">action（已校验不含引号与换行）。</param>
    /// <returns>头值。</returns>
    public static string SoapActionHeaderValue(string action) => $"\"{action}\"";
}
