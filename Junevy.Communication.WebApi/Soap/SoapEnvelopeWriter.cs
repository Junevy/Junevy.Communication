using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// 把 Body 元素（与可选的 Header 元素）写成 SOAP 信封的字节（内部）：UTF-8，不带 BOM，带 XML 声明，不缩进。
/// 写出的是调用方元素的副本，调用方的元素不会被挂到信封下面。
/// </summary>
internal static class SoapEnvelopeWriter
{
    private const string SoapPrefix = "soap";

    /// <summary>
    /// 写出信封。
    /// </summary>
    /// <param name="version">协议版本。</param>
    /// <param name="body">Body 的子元素。</param>
    /// <param name="headers">写入 <c>soap:Header</c> 的元素；为空时不写 Header 元素。</param>
    /// <returns>信封的 UTF-8 字节。</returns>
    public static byte[] Write(SoapVersion version, XElement body, IEnumerable<XElement> headers)
    {
        XNamespace soap = SoapProtocol.NamespaceOf(version);
        var envelope = new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + SoapPrefix, soap));
        XElement[] headerCopies = headers.Select(header => new XElement(header)).ToArray();
        if (headerCopies.Length > 0)
            envelope.Add(new XElement(soap + "Header", headerCopies));

        envelope.Add(new XElement(soap + "Body", new XElement(body)));

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false };
        using var stream = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(stream, settings))
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), envelope).Save(writer);
        }

        return stream.ToArray();
    }
}
