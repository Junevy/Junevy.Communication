using System.Xml;
using System.Xml.Linq;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// 解析后的 SOAP 响应信封（内部）：不是信封，或是 Fault，或是 Body 的第一个子元素（Body 为空时为 null）。
/// 解析时 <c>DtdProcessing = Prohibit</c>、<c>XmlResolver = null</c>：带文档类型声明的响应一律拒绝，外部实体永远不会被解析（防 XXE，P3-D17）。
/// 信封的版本按根元素自己的命名空间判定，不依赖客户端配置的版本。
/// </summary>
internal sealed class SoapMessage
{
    private const string DefaultFaultMessage = "The SOAP service returned a fault.";

    private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    private SoapMessage(bool isEnvelope, XElement? content, string? faultCode, string? faultMessage)
    {
        IsEnvelope = isEnvelope;
        Content = content;
        FaultCode = faultCode;
        FaultMessage = faultMessage;
    }

    /// <summary>是不是 SOAP 信封（根是 SOAP 1.1 或 1.2 命名空间下的 Envelope，且有 Body）。</summary>
    public bool IsEnvelope { get; }

    /// <summary>Body 的第一个子元素；不是 Fault 且 Body 非空时有值。</summary>
    public XElement? Content { get; }

    /// <summary>Body 的第一个子元素是不是 Fault。</summary>
    public bool IsFault => FaultCode != null;

    /// <summary>Fault 的代码原文（1.1 的 faultcode，1.2 的 Code/Value）；缺失时为空串，因此是 Fault 就一定非 null。不是 Fault 时为 null。</summary>
    public string? FaultCode { get; }

    /// <summary>Fault 的消息（1.1 的 faultstring，1.2 的第一个 Reason/Text，有 Subcode 时附 " (subcode: 值)"）；不是 Fault 时为 null。</summary>
    public string? FaultMessage { get; }

    /// <summary>
    /// 解析响应正文。不是 XML、含 DTD、不是 SOAP 信封都不抛异常，返回"不是信封"。
    /// </summary>
    /// <param name="body">响应正文。</param>
    /// <returns>解析结果。</returns>
    public static SoapMessage Parse(byte[] body)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(body);
            using XmlReader reader = XmlReader.Create(stream, ReaderSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return NotAnEnvelope();
        }

        XElement? root = document.Root;
        XNamespace ns = root?.Name.Namespace ?? XNamespace.None;
        if (root == null || root.Name.LocalName != "Envelope" || (ns != SoapProtocol.Soap11Namespace && ns != SoapProtocol.Soap12Namespace))
            return NotAnEnvelope();

        XElement? soapBody = root.Element(ns + "Body");
        if (soapBody == null)
            return NotAnEnvelope();

        XElement? first = soapBody.Elements().FirstOrDefault();
        if (first != null && first.Name == ns + "Fault")
            return ns == SoapProtocol.Soap12Namespace ? ReadFault12(first, ns) : ReadFault11(first);

        return new SoapMessage(true, first, null, null);
    }

    private static SoapMessage NotAnEnvelope() => new SoapMessage(false, null, null, null);

    private static SoapMessage ReadFault11(XElement fault)
        => new SoapMessage(true, null, fault.Element("faultcode")?.Value ?? string.Empty, fault.Element("faultstring")?.Value ?? DefaultFaultMessage);

    private static SoapMessage ReadFault12(XElement fault, XNamespace ns)
    {
        XElement? code = fault.Element(ns + "Code");
        string codeValue = code?.Element(ns + "Value")?.Value ?? string.Empty;
        string? subcode = code?.Element(ns + "Subcode")?.Element(ns + "Value")?.Value;
        string reason = fault.Element(ns + "Reason")?.Elements(ns + "Text").FirstOrDefault()?.Value ?? DefaultFaultMessage;
        return new SoapMessage(true, null, codeValue, subcode == null ? reason : $"{reason} (subcode: {subcode})");
    }
}
