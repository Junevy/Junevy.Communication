namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// SOAP 协议版本。1.1 用 <c>text/xml</c> 加 <c>SOAPAction</c> 头；1.2 用 <c>application/soap+xml</c>，action 放在内容类型的参数里。
/// </summary>
public enum SoapVersion
{
    /// <summary>SOAP 1.1（命名空间 <c>http://schemas.xmlsoap.org/soap/envelope/</c>），默认。</summary>
    Soap11 = 0,

    /// <summary>SOAP 1.2（命名空间 <c>http://www.w3.org/2003/05/soap-envelope</c>）。</summary>
    Soap12 = 1,
}
