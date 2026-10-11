using System.Text;
using System.Xml.Linq;
using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="SoapRequestBuilder"/> 的测试（计划 Task 9 第 1 条）：两份黄金信封按字节比较，SOAPAction 与 Content-Type 的各种 action 值，
/// SoapHeaders、DefaultNamespace、参数校验。
/// </summary>
public sealed class SoapRequestBuilderTests
{
    private const string Ns = SoapTestNamespaces.Mes;

    private const string Golden11 =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        + "<soap:Body><GetOrder xmlns=\"http://mes.local/ns\"><OrderNo>工单-1</OrderNo></GetOrder></soap:Body>"
        + "</soap:Envelope>";

    private const string Golden12 =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">"
        + "<soap:Header><Auth xmlns=\"http://mes.local/ns\"><Token>t-1</Token></Auth></soap:Header>"
        + "<soap:Body><GetOrder xmlns=\"http://mes.local/ns\"><OrderNo>A-1</OrderNo></GetOrder></soap:Body>"
        + "</soap:Envelope>";

    [Fact]
    public void Soap11_TypedBody_ProducesTheGoldenEnvelopeByteForByte()
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "urn:GetOrder", new GetOrder { OrderNo = "工单-1" });

        Assert.Equal(Encoding.UTF8.GetBytes(Golden11), snapshot.Body);
        Assert.Equal("text/xml; charset=utf-8", snapshot.ContentType);
        Assert.Equal("\"urn:GetOrder\"", snapshot.Headers["SOAPAction"]);
        Assert.Equal("POST", snapshot.Method);
        Assert.Equal(string.Empty, snapshot.RelativeUri);
    }

    [Fact]
    public void Soap12_RawBodyWithAHeader_ProducesTheGoldenEnvelopeByteForByte()
    {
        var options = new SoapCallOptions
        {
            SoapHeaders = new List<XElement> { new XElement(XName.Get("Auth", Ns), new XElement(XName.Get("Token", Ns), "t-1")) },
        };
        XElement body = new XElement(XName.Get("GetOrder", Ns), new XElement(XName.Get("OrderNo", Ns), "A-1"));

        HttpRequestSnapshot snapshot = BuildRaw(SoapVersion.Soap12, "urn:GetOrder", body, options);

        Assert.Equal(Encoding.UTF8.GetBytes(Golden12), snapshot.Body);
        Assert.Equal("application/soap+xml; charset=utf-8; action=\"urn:GetOrder\"", snapshot.ContentType);
        Assert.False(snapshot.Headers.ContainsKey("SOAPAction"));
    }

    [Fact]
    public void Envelope_HasNoByteOrderMark()
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "a", new GetOrder { OrderNo = "x" });

        Assert.Equal((byte)'<', snapshot.Body[0]);
    }

    [Theory]
    [InlineData("urn:GetOrder", "\"urn:GetOrder\"")]
    [InlineData("http://mes.local/ns/GetOrder", "\"http://mes.local/ns/GetOrder\"")]
    [InlineData("", "\"\"")]
    public void Soap11_SoapActionIsAlwaysQuoted(string action, string expectedHeader)
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, action, new GetOrder { OrderNo = "x" });

        Assert.Equal(expectedHeader, snapshot.Headers["SOAPAction"]);
        Assert.Equal("text/xml; charset=utf-8", snapshot.ContentType);
    }

    [Theory]
    [InlineData("urn:GetOrder", "application/soap+xml; charset=utf-8; action=\"urn:GetOrder\"")]
    [InlineData("", "application/soap+xml; charset=utf-8")]
    public void Soap12_ActionTravelsInTheContentType_AndIsOmittedWhenEmpty(string action, string expectedContentType)
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap12, action, new GetOrder { OrderNo = "x" });

        Assert.Equal(expectedContentType, snapshot.ContentType);
        Assert.False(snapshot.Headers.ContainsKey("SOAPAction"));
    }

    [Fact]
    public void DefaultNamespace_IsUsedForTypesWithoutNamespaceAttributes()
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "ping", new Ping { Seq = 7 }, defaultNamespace: Ns);

        string xml = Encoding.UTF8.GetString(snapshot.Body);
        Assert.Contains("<Ping xmlns=\"http://mes.local/ns\"><Seq>7</Seq></Ping>", xml);
    }

    [Fact]
    public void WithoutADefaultNamespace_TypesWithoutAttributesStayInNoNamespace()
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "ping", new Ping { Seq = 7 });

        Assert.Contains("<Ping><Seq>7</Seq></Ping>", Encoding.UTF8.GetString(snapshot.Body));
    }

    [Fact]
    public void SoapHeaders_AreWrittenInOrderInsideTheHeaderElement()
    {
        var options = new SoapCallOptions { SoapHeaders = new List<XElement> { new XElement("First"), new XElement("Second", "x") } };

        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "a", new Ping { Seq = 1 }, options);

        Assert.Contains("<soap:Header><First /><Second>x</Second></soap:Header><soap:Body>", Encoding.UTF8.GetString(snapshot.Body));
    }

    [Fact]
    public void NoSoapHeaders_OmitsTheHeaderElement()
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "a", new Ping { Seq = 1 }, new SoapCallOptions { SoapHeaders = new List<XElement>() });

        Assert.DoesNotContain("Header", Encoding.UTF8.GetString(snapshot.Body));
    }

    [Fact]
    public void RawBody_IsCopied_SoTheCallersElementIsNotReparented()
    {
        XElement body = new XElement("Echo", "hi");

        BuildRaw(SoapVersion.Soap11, "a", body, null);

        Assert.Null(body.Parent);
    }

    [Fact]
    public void HttpHeadersIdempotencyKeyAndDefaultHeaders_AreMergedIntoTheSnapshot()
    {
        EndpointSettings endpoint = EndpointConfigRules.CreateSettings(new TestEndpointConfig
        {
            DefaultHeaders = new Dictionary<string, string> { ["X-Line"] = "L3", ["X-Shared"] = "default" },
        });
        var options = new SoapCallOptions
        {
            Headers = new Dictionary<string, string> { ["X-Shared"] = "call", ["SOAPAction"] = "\"hijack\"" },
            IdempotencyKey = "key-1",
        };

        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "urn:A", new Ping { Seq = 1 }, options, endpoint: endpoint);

        Assert.Equal("L3", snapshot.Headers["X-Line"]);
        Assert.Equal("call", snapshot.Headers["X-Shared"]);
        Assert.Equal("key-1", snapshot.Headers["Idempotency-Key"]);
        Assert.Equal("key-1", snapshot.IdempotencyKey);
        Assert.Equal("\"urn:A\"", snapshot.Headers["SOAPAction"]);
    }

    [Fact]
    public void ChineseText_IsWrittenAsUtf8()
    {
        HttpRequestSnapshot snapshot = Build(SoapVersion.Soap11, "a", new GetOrder { OrderNo = "工单" });

        Assert.Contains("工单", Encoding.UTF8.GetString(snapshot.Body));
        Assert.DoesNotContain("&#", Encoding.UTF8.GetString(snapshot.Body));
    }

    public static IEnumerable<object[]> InvalidCalls()
    {
        yield return new object[] { "null action", null!, (Func<SoapCallOptions?>)(() => null) };
        yield return new object[] { "quote in action", "bad\"action", (Func<SoapCallOptions?>)(() => null) };
        yield return new object[] { "newline in action", "bad\r\naction", (Func<SoapCallOptions?>)(() => null) };
        yield return new object[] { "zero timeout", "a", (Func<SoapCallOptions?>)(() => new SoapCallOptions { Timeout = 0 }) };
        yield return new object[] { "negative timeout", "a", (Func<SoapCallOptions?>)(() => new SoapCallOptions { Timeout = -1 }) };
        yield return new object[] { "null soap header", "a", (Func<SoapCallOptions?>)(() => new SoapCallOptions { SoapHeaders = new List<XElement> { null! } }) };
    }

    [Theory]
    [MemberData(nameof(InvalidCalls))]
    public void TryBuild_InvalidCall_FailsWithAnEnglishMessage(string scenario, string action, Func<SoapCallOptions?> options)
    {
        Assert.NotNull(scenario);
        var builder = new SoapRequestBuilder(Endpoint(), SoapVersion.Soap11);

        bool built = builder.TryBuild(action, () => new XElement("Body"), options(), out HttpRequestSnapshot? snapshot, out string? error);

        Assert.False(built);
        Assert.Null(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryBuild_NullBody_Fails()
    {
        var builder = new SoapRequestBuilder(Endpoint(), SoapVersion.Soap11);

        bool built = builder.TryBuild("a", () => null, null, out _, out string? error);

        Assert.False(built);
        Assert.Contains("body", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuild_ABodyThatCannotBeSerialized_FailsWithTheReason()
    {
        var builder = new SoapRequestBuilder(Endpoint(), SoapVersion.Soap11);

        bool built = builder.TryBuild("a", () => throw new InvalidOperationException("no mapping for the type"), null, out _, out string? error);

        Assert.False(built);
        Assert.Contains("no mapping for the type", error);
    }

    private static HttpRequestSnapshot Build<T>(SoapVersion version, string action, T body, SoapCallOptions? options = null, string? defaultNamespace = null,
                                                EndpointSettings? endpoint = null)
    {
        var builder = new SoapRequestBuilder(endpoint ?? Endpoint(), version);
        var codec = new XmlBodyCodec(defaultNamespace);
        Assert.True(builder.TryBuild(action, () => codec.Serialize(body), options, out HttpRequestSnapshot? snapshot, out string? error), error);
        return snapshot!;
    }

    private static HttpRequestSnapshot BuildRaw(SoapVersion version, string action, XElement body, SoapCallOptions? options)
    {
        var builder = new SoapRequestBuilder(Endpoint(), version);
        Assert.True(builder.TryBuild(action, () => body, options, out HttpRequestSnapshot? snapshot, out string? error), error);
        return snapshot!;
    }

    private static EndpointSettings Endpoint() => EndpointConfigRules.CreateSettings(new TestEndpointConfig());
}
