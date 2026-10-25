using System.Net;
using System.Net.Http;
using System.Text;
using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.WebApi;

/// <summary>
/// SOAP 客户端页（计划 Task 18 第 2 条）：经脚本化的 HTTP 处理器（<c>WebApiComponents.HttpHandler</c>）测试原始调用、
/// SOAP 1.1 的 SOAPAction 与 SOAP 1.2 的 Content-Type、Fault 映射（远端码为 fault code，且不重试），以及输入不合法时不发送。
/// </summary>
public sealed class SoapClientSessionViewModelTests
{
    private const string SuccessEnvelope11 = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <GetOrderResponse xmlns="http://mes.example.com/ns"><Status>OK</Status></GetOrderResponse>
          </soap:Body>
        </soap:Envelope>
        """;

    private const string SuccessEnvelope12 = """
        <?xml version="1.0" encoding="utf-8"?>
        <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope">
          <env:Body>
            <GetOrderResponse xmlns="http://mes.example.com/ns"><Status>OK</Status></GetOrderResponse>
          </env:Body>
        </env:Envelope>
        """;

    private const string FaultEnvelope11 = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <soap:Fault><faultcode>soap:Server</faultcode><faultstring>order not found</faultstring></soap:Fault>
          </soap:Body>
        </soap:Envelope>
        """;

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static SoapClientSessionViewModel CreatePage(ScriptedHttpHandler handler) =>
        new(ProtocolCatalog.Get(ProtocolKind.SoapClient), FakeServices.Create(new ManualUiDispatcher()), handler);

    [Fact]
    public async Task Invoke_SendsTheSoapAction_AndTheEnvelope_AndShowsTheResponseXml()
    {
        using var handler = new ScriptedHttpHandler(_ => Respond(HttpStatusCode.OK, SuccessEnvelope11, "text/xml"));
        await using var page = CreatePage(handler);
        page.SoapAction = "urn:GetOrder";
        page.BodyXml = "<GetOrder><OrderNo>A-1</OrderNo></GetOrder>";
        await page.ConnectCommand.ExecuteAsync(null);

        await page.SendCommand.ExecuteAsync(null);

        ScriptedHttpHandler.CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(("POST", "\"urn:GetOrder\""), (request.Method, request.Headers["SOAPAction"]));
        Assert.Contains("<OrderNo>A-1</OrderNo>", request.Body);
        Assert.Equal(("200", "成功"), (page.ResponseStatus, page.ResponseErrorKind));
        Assert.Contains("<Status>OK</Status>", page.ResponseXml);
    }

    [Fact]
    public async Task Soap12_UsesTheSoap12Envelope_AndContentType()
    {
        using var handler = new ScriptedHttpHandler(_ => Respond(HttpStatusCode.OK, SuccessEnvelope12, "application/soap+xml"));
        await using var page = CreatePage(handler);
        page.Version = SoapVersion.Soap12;
        page.SoapAction = "urn:GetOrder";
        await page.ConnectCommand.ExecuteAsync(null);

        await page.SendCommand.ExecuteAsync(null);

        ScriptedHttpHandler.CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Contains("application/soap+xml", request.Headers["Content-Type"]);
        Assert.Contains("http://www.w3.org/2003/05/soap-envelope", request.Body);
        Assert.Equal("成功", page.ResponseErrorKind);
    }

    [Fact]
    public async Task Fault_IsARemoteError_WithTheFaultCode_AndIsNeverRetried()
    {
        using var handler = new ScriptedHttpHandler(_ => Respond(HttpStatusCode.InternalServerError, FaultEnvelope11, "text/xml"));
        await using var page = CreatePage(handler);
        page.RetryCount = 2;
        page.RetryDelayMs = 10;
        await page.ConnectCommand.ExecuteAsync(null);

        await page.SendCommand.ExecuteAsync(null);

        Assert.Single(handler.Requests);
        Assert.Equal(("500", "soap:Server", "对端返回错误"), (page.ResponseStatus, page.ResponseRemoteCode, page.ResponseErrorKind));
        Assert.Contains("order not found", page.ResponseMessage);
    }

    [Fact]
    public async Task AnEmptyActionOrInvalidXml_IsRefused_BeforeAnythingIsSent()
    {
        using var handler = new ScriptedHttpHandler(_ => Respond(HttpStatusCode.OK, SuccessEnvelope11, "text/xml"));
        await using var page = CreatePage(handler);
        await page.ConnectCommand.ExecuteAsync(null);

        page.SoapAction = " ";
        await page.SendCommand.ExecuteAsync(null);
        Assert.Equal("SOAPAction 不能为空。", page.RequestMessage);

        page.SoapAction = "urn:GetOrder";
        page.BodyXml = "<open>";
        await page.SendCommand.ExecuteAsync(null);
        Assert.StartsWith("正文不是有效的 XML：", page.RequestMessage);

        Assert.Empty(handler.Requests);
    }
}
