using System.Text;
using System.Xml.Linq;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="SoapResponseDecoder"/> 的测试（计划 Task 9 第 2 条）：Fault 先于状态码解析、1.1 与 1.2 的 Fault 结构、XXE 防护、正文不是 XML 时的处理。
/// </summary>
public sealed class SoapResponseDecoderTests
{
    private const string Ns = SoapTestNamespaces.Mes;

    private const string Fault11 =
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>"
        + "<soap:Fault><faultcode>soap:Server</faultcode><faultstring>Server was unable to process request.</faultstring><detail /></soap:Fault>"
        + "</soap:Body></soap:Envelope>";

    // 结构取自 W3C SOAP 1.2 Primer 的 Sender / MessageTimeout 示例。
    private const string Fault12 =
        "<env:Envelope xmlns:env=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:m=\"http://www.example.org/timeouts\">"
        + "<env:Body><env:Fault>"
        + "<env:Code><env:Value>env:Sender</env:Value><env:Subcode><env:Value>m:MessageTimeout</env:Value></env:Subcode></env:Code>"
        + "<env:Reason><env:Text xml:lang=\"en\">Sender Timeout</env:Text></env:Reason>"
        + "<env:Detail><m:MaxTime>P5M</m:MaxTime></env:Detail>"
        + "</env:Fault></env:Body></env:Envelope>";

    [Fact]
    public void Soap11Fault_WithStatus500_IsARemoteErrorWithFaultcodeAndFaultstring()
    {
        WebApiResult<XElement> result = Decode(500, Fault11, reason: "Internal Server Error");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("soap:Server", result.RemoteCode);
        Assert.Equal("Server was unable to process request.", result.ErrorMessage);
        Assert.Equal(500, result.StatusCode);
    }

    [Fact]
    public void Soap12Fault_UsesCodeValueReasonTextAndAppendsTheSubcode()
    {
        WebApiResult<XElement> result = Decode(500, Fault12);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("env:Sender", result.RemoteCode);
        Assert.Equal("Sender Timeout (subcode: m:MessageTimeout)", result.ErrorMessage);
    }

    [Fact]
    public void Soap12Fault_WithoutASubcode_HasNoSuffix()
    {
        string fault = "<env:Envelope xmlns:env=\"http://www.w3.org/2003/05/soap-envelope\"><env:Body><env:Fault>"
                       + "<env:Code><env:Value>env:Receiver</env:Value></env:Code><env:Reason><env:Text>Broken</env:Text></env:Reason>"
                       + "</env:Fault></env:Body></env:Envelope>";

        WebApiResult<XElement> result = Decode(500, fault);

        Assert.Equal("env:Receiver", result.RemoteCode);
        Assert.Equal("Broken", result.ErrorMessage);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(503)]
    public void Fault_TakesPrecedenceOverTheStatusCode(int status)
    {
        WebApiResult<XElement> result = Decode(status, Fault11);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("soap:Server", result.RemoteCode);
        Assert.Equal(status, result.StatusCode);
    }

    [Fact]
    public void Fault_WithoutACode_StillCarriesARemoteCodeSoItIsNeverRetried()
    {
        string fault = "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>"
                       + "<faultstring>oops</faultstring></soap:Fault></soap:Body></soap:Envelope>";

        WebApiResult<XElement> result = Decode(500, fault);

        Assert.Equal(string.Empty, result.RemoteCode);
        Assert.Equal("oops", result.ErrorMessage);
    }

    [Fact]
    public void SuccessfulResponse_RawReturnsTheFirstChildOfTheBody()
    {
        WebApiResult<XElement> result = Decode(200, SoapRig.Soap11Response($"<GetOrderResponse xmlns=\"{Ns}\"><Status>OK</Status></GetOrderResponse>"));

        Assert.True(result.IsSuccess);
        Assert.Equal(XName.Get("GetOrderResponse", Ns), result.Data!.Name);
        Assert.Equal("OK", result.Data.Element(XName.Get("Status", Ns))!.Value);
    }

    [Fact]
    public void SuccessfulResponse_TypedIsDeserializedFromTheFirstChild()
    {
        var codec = new XmlBodyCodec(null);

        WebApiResult<GetOrderResponse> result = Decoder().Decode(
            Response(200, SoapRig.Soap11Response($"<GetOrderResponse xmlns=\"{Ns}\"><Status>已完成</Status><Quantity>12</Quantity></GetOrderResponse>")),
            codec.Deserialize<GetOrderResponse>);

        Assert.True(result.IsSuccess);
        Assert.Equal("已完成", result.Data!.Status);
        Assert.Equal(12, result.Data.Quantity);
    }

    [Fact]
    public void SuccessfulResponse_ThatCannotBeDeserialized_IsAProtocolViolation()
    {
        var codec = new XmlBodyCodec(null);

        WebApiResult<GetOrderResponse> result = Decoder().Decode(Response(200, SoapRig.Soap11Response("<Unexpected xmlns=\"urn:other\"/>")), codec.Deserialize<GetOrderResponse>);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Exception);
    }

    [Theory]
    [InlineData("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body /></soap:Envelope>")]
    [InlineData("")]
    public void EmptyBody_OnSuccess_IsSuccessWithDefaultData(string body)
    {
        WebApiResult<XElement> result = Decode(200, body);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Soap12Reply_ToAnyClient_IsReadAccordingToItsOwnNamespace()
    {
        string reply = "<env:Envelope xmlns:env=\"http://www.w3.org/2003/05/soap-envelope\"><env:Body><Pong /></env:Body></env:Envelope>";

        WebApiResult<XElement> result = Decode(200, reply);

        Assert.Equal("Pong", result.Data!.Name.LocalName);
    }

    [Theory]
    [InlineData(404, CommErrorKind.RemoteError, "HTTP 404 Not Found.")]
    [InlineData(401, CommErrorKind.AuthenticationFailed, "HTTP 401 Not Found.")]
    [InlineData(429, CommErrorKind.ResourceExhausted, "HTTP 429 Not Found.")]
    [InlineData(503, CommErrorKind.RemoteError, "HTTP 503 Not Found.")]
    public void NonSuccessStatusWithoutAFault_IsClassifiedByTheStatusCode(int status, CommErrorKind kind, string message)
    {
        WebApiResult<XElement> result = Decode(status, SoapRig.Soap11Response("<Info />"), reason: "Not Found");

        Assert.Equal(kind, result.ErrorKind);
        Assert.Equal(message, result.ErrorMessage);
        Assert.Null(result.RemoteCode);
    }

    [Theory]
    [InlineData("<html>Bad Gateway</html>")]
    [InlineData("plain text")]
    [InlineData("")]
    [InlineData("<other><Body/></other>")]
    public void NonSuccessStatus_WithABodyThatIsNotASoapEnvelope_IsClassifiedByTheStatusCode(string body)
    {
        WebApiResult<XElement> result = Decode(502, body, reason: "Bad Gateway");

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("HTTP 502 Bad Gateway.", result.ErrorMessage);
        Assert.Null(result.RemoteCode);
    }

    [Theory]
    [InlineData("<html>not soap</html>")]
    [InlineData("plain text")]
    [InlineData("<other><Body/></other>")]
    [InlineData("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Header /></soap:Envelope>")]
    public void SuccessStatus_WithABodyThatIsNotASoapEnvelope_IsAProtocolViolation(string body)
    {
        WebApiResult<XElement> result = Decode(200, body);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public void ADocumentTypeDeclaration_IsRejected_AndExternalEntitiesAreNeverResolved()
    {
        string xxe = "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY secret SYSTEM \"file:///c:/windows/win.ini\">]>"
                     + "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><Echo>&secret;</Echo></soap:Body></soap:Envelope>";

        WebApiResult<XElement> result = Decode(200, xxe);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ADocumentTypeDeclaration_InAnErrorResponse_FallsBackToTheStatusClassification()
    {
        string xxe = "<!DOCTYPE foo [<!ENTITY a \"b\">]><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault><faultcode>x</faultcode>"
                     + "<faultstring>&a;</faultstring></soap:Fault></soap:Body></soap:Envelope>";

        WebApiResult<XElement> result = Decode(500, xxe, reason: "Internal Server Error");

        Assert.Equal("HTTP 500 Internal Server Error.", result.ErrorMessage);
        Assert.Null(result.RemoteCode);
    }

    [Fact]
    public void AnExtractorThatThrows_IsAProtocolViolationWithTheException()
    {
        WebApiResult<string> result = Decoder().Decode<string>(Response(200, SoapRig.Soap11Response("<A />")), _ => throw new FormatException("bad value"));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.IsType<FormatException>(result.Exception);
    }

    private static WebApiResult<XElement> Decode(int status, string body, string? reason = null)
        => Decoder().Decode(Response(status, body, reason), element => element);

    private static SoapResponseDecoder Decoder() => new SoapResponseDecoder();

    private static HttpExchangeResponse Response(int status, string body, string? reason = null)
        => new HttpExchangeResponse(status, reason, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body), "text/xml; charset=utf-8");
}
