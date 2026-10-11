using System.Text;
using System.Xml.Linq;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="XmlBodyCodec"/> 的测试：强类型正文与 XML 元素之间的转换，以及 XmlSerializer 的缓存（只用两个会缓存生成程序集的构造函数）。
/// </summary>
public sealed class XmlBodyCodecTests
{
    private const string Ns = SoapTestNamespaces.Mes;

    [Fact]
    public void Serialize_WritesTheTypedObjectWithoutXsiAndXsdNamespaceNoise()
    {
        var codec = new XmlBodyCodec(null);

        XElement element = codec.Serialize(new GetOrder { OrderNo = "A-1" });

        Assert.Equal("<GetOrder xmlns=\"http://mes.local/ns\"><OrderNo>A-1</OrderNo></GetOrder>", element.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Deserialize_ReadsTheTypedObjectFromTheElement()
    {
        var codec = new XmlBodyCodec(null);
        var element = new XElement(XName.Get("GetOrderResponse", Ns), new XElement(XName.Get("Status", Ns), "OK"), new XElement(XName.Get("Quantity", Ns), 5));

        GetOrderResponse response = codec.Deserialize<GetOrderResponse>(element);

        Assert.Equal("OK", response.Status);
        Assert.Equal(5, response.Quantity);
    }

    [Fact]
    public void Deserialize_AnElementOfAnotherShape_Throws()
    {
        var codec = new XmlBodyCodec(null);

        Assert.ThrowsAny<InvalidOperationException>(() => codec.Deserialize<GetOrderResponse>(new XElement("Other")));
    }

    [Fact]
    public void RoundTrip_PreservesChineseText()
    {
        var codec = new XmlBodyCodec(null);
        XElement element = codec.Serialize(new GetOrder { OrderNo = "工单 <&> 1" });

        GetOrder back = codec.Deserialize<GetOrder>(element);

        Assert.Equal("工单 <&> 1", back.OrderNo);
    }

    [Theory]
    [InlineData(5)]
    [InlineData("text")]
    [InlineData(true)]
    [InlineData(2.5)]
    public void ObjectTypedMember_SurvivesTheEnvelopeRoundTrip(object value)
    {
        var codec = new XmlBodyCodec(null);

        VariantBody back = RoundTripThroughAnEnvelope(codec, new VariantBody { Value = value, Note = "n", Count = 3 });

        Assert.Equal(value, back.Value);
        Assert.Equal("n", back.Note);
        Assert.Equal(3, back.Count);
    }

    [Fact]
    public void NilMembers_SurviveTheEnvelopeRoundTrip()
    {
        var codec = new XmlBodyCodec(null);

        VariantBody back = RoundTripThroughAnEnvelope(codec, new VariantBody { Value = null, Note = null, Count = null });

        Assert.Null(back.Value);
        Assert.Null(back.Note);
        Assert.Null(back.Count);
    }

    [Fact]
    public void TheEnvelope_KeepsTheBodyInTheDefaultNamespace_AndEveryQNameValuedAttributePrefixIsBound()
    {
        var codec = new XmlBodyCodec(null);
        XElement body = codec.Serialize(new VariantBody { Value = 5, Note = null });
        byte[] envelope = SoapEnvelopeWriter.Write(SoapVersion.Soap11, body, Enumerable.Empty<XElement>());

        Assert.Contains("<Variant xmlns=\"http://mes.local/ns\"", Encoding.UTF8.GetString(envelope));
        XElement parsed = SoapMessage.Parse(envelope).Content!;
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        XElement value = parsed.Element(XName.Get("Value", Ns))!;
        string[] type = value.Attribute(xsi + "type")!.Value.Split(':');
        Assert.Equal("http://www.w3.org/2001/XMLSchema", value.GetNamespaceOfPrefix(type[0])?.NamespaceName);
        Assert.Equal("int", type[1]);
        Assert.Equal("true", parsed.Element(XName.Get("Note", Ns))!.Attribute(xsi + "nil")!.Value);
    }

    [Fact]
    public void ADefaultNamespaceBody_IsStillWrittenWithTheDefaultNamespace()
    {
        var codec = new XmlBodyCodec(Ns);

        XElement element = codec.Serialize(new Ping { Seq = 4 });

        Assert.Equal("<Ping xmlns=\"http://mes.local/ns\"><Seq>4</Seq></Ping>", element.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(4, codec.Deserialize<Ping>(element).Seq);
    }

    private static T RoundTripThroughAnEnvelope<T>(XmlBodyCodec codec, T body)
    {
        byte[] envelope = SoapEnvelopeWriter.Write(SoapVersion.Soap11, codec.Serialize(body), Enumerable.Empty<XElement>());
        SoapMessage message = SoapMessage.Parse(envelope);
        Assert.True(message.IsEnvelope);
        return codec.Deserialize<T>(message.Content!);
    }
}
