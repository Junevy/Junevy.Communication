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

    [Fact]
    public void Serializers_AreCachedPerTypeAndNamespace()
    {
        var codec = new XmlBodyCodec("urn:a");

        var first = codec.GetSerializer(typeof(GetOrder));
        var second = codec.GetSerializer(typeof(GetOrder));
        var other = codec.GetSerializer(typeof(Ping));

        Assert.Same(first, second);
        Assert.NotSame(first, other);
    }

    [Fact]
    public async Task ConcurrentUse_CreatesEachSerializerOnce()
    {
        var codec = new XmlBodyCodec(null);

        var serializers = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => codec.GetSerializer(typeof(GetOrder)))));

        Assert.All(serializers, serializer => Assert.Same(serializers[0], serializer));
    }
}
