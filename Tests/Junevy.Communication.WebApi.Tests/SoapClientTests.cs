using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="SoapClient"/> 的端到端测试（用 <see cref="StubHttpHandler"/>，不开套接字）：构造、名称、线路格式、强类型与原始 XML 往返、
/// Fault 不重试、事件转发、健康检查与释放。信封的字节与 Fault 解析的细节见 <c>SoapRequestBuilderTests</c> / <c>SoapResponseDecoderTests</c>。
/// </summary>
public sealed class SoapClientTests
{
    private const string Ns = SoapTestNamespaces.Mes;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static readonly string ServerFault = "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>"
                                                  + "<faultcode>soap:Server</faultcode><faultstring>station locked</faultstring></soap:Fault></soap:Body></soap:Envelope>";

    [Fact]
    public async Task Name_DefaultsToTheAuthorityOfTheUrl()
    {
        await using var plain = new SoapRig(name: null);
        await using var withPort = new SoapRig(c => c.Url = "https://mes.local:8443/svc", name: null);

        Assert.Equal("mes.local", plain.Client.Name);
        Assert.Equal("mes.local:8443", withPort.Client.Name);
    }

    [Fact]
    public async Task Name_CanBeGivenExplicitly()
    {
        await using var rig = new SoapRig(name: "line-3", script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<A />"), "text/xml"));

        await rig.Client.InvokeRawAsync("urn:A", new XElement("A"));

        Assert.Equal("line-3", rig.Client.Name);
        Assert.Equal("line-3", rig.Completed.Single().EndpointName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ExplicitName_MustNotBeBlank(string name)
    {
        Assert.Throws<ArgumentException>(() => new SoapRig(name: name));
    }

    [Fact]
    public void Constructor_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SoapClient(null!));
        Assert.Throws<ArgumentNullException>(() => new SoapClient("mes", null!));
    }

    [Fact]
    public void Constructor_InvalidConfig_ThrowsArgumentExceptionNamingTheField()
    {
        var exception = Assert.Throws<ArgumentException>(() => new SoapRig(c => c.Url = "not a url"));

        Assert.Equal("Url", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithASerializerComponent_ThrowsBecauseSoapUsesXml()
    {
        var exception = Assert.Throws<ArgumentException>(() => new SoapRig(configureComponents: components => components.Serializer = new SystemTextJsonSerializerStub()));

        Assert.Equal("components", exception.ParamName);
        Assert.Contains("Serializer", exception.Message);
    }

    [Fact]
    public async Task Config_ReturnsTheObjectGivenToTheConstructor_AndLaterChangesDoNotAffectTheClient()
    {
        SoapClientConfig? given = null;
        await using var rig = new SoapRig(c => given = c, script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<A />"), "text/xml"));

        given!.Url = "http://elsewhere.local/";
        await rig.Client.InvokeRawAsync("urn:A", new XElement("A"));

        Assert.Same(given, rig.Client.Config);
        Assert.Equal("http://mes.local/service.asmx", rig.Handler.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task InvokeAsync_Typed_PutsTheGoldenRequestOnTheWire_AndReadsTheTypedResponse()
    {
        await using var rig = new SoapRig(c => c.DefaultHeaders["X-Line"] = "L3", script: handler =>
            handler.Enqueue(200, SoapRig.Soap11Response($"<GetOrderResponse xmlns=\"{Ns}\"><Status>完成</Status><Quantity>4</Quantity></GetOrderResponse>"), "text/xml; charset=utf-8"));

        WebApiResult<GetOrderResponse> result = await rig.Client.InvokeAsync<GetOrder, GetOrderResponse>("urn:GetOrder", new GetOrder { OrderNo = "工单-1" });

        Assert.True(result.IsSuccess);
        Assert.Equal("完成", result.Data!.Status);
        Assert.Equal(4, result.Data.Quantity);
        StubRequest sent = Assert.Single(rig.Handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("\"urn:GetOrder\"", sent.Headers["SOAPAction"]);
        Assert.Equal("L3", sent.Headers["X-Line"]);
        Assert.Equal("text/xml; charset=utf-8", sent.ContentHeaders["Content-Type"]);
        string expected = "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">"
                          + "<soap:Body><GetOrder xmlns=\"http://mes.local/ns\"><OrderNo>工单-1</OrderNo></GetOrder></soap:Body></soap:Envelope>";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), sent.Body);
    }

    [Fact]
    public async Task InvokeRawAsync_RoundTripsAnXmlElement()
    {
        await using var rig = new SoapRig(script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<Echo xmlns=\"urn:x\">pong</Echo>"), "text/xml"));

        WebApiResult<XElement> result = await rig.Client.InvokeRawAsync("urn:Echo", new XElement(XName.Get("Echo", "urn:x"), "ping"));

        Assert.Equal("pong", result.Data!.Value);
        Assert.Contains("<Echo xmlns=\"urn:x\">ping</Echo>", Encoding.UTF8.GetString(rig.Handler.Requests.Single().Body));
    }

    [Fact]
    public async Task Soap12_UsesTheSoapPlusXmlContentType()
    {
        await using var rig = new SoapRig(c => c.Version = SoapVersion.Soap12, script: handler =>
            handler.Enqueue(200, "<env:Envelope xmlns:env=\"http://www.w3.org/2003/05/soap-envelope\"><env:Body><A /></env:Body></env:Envelope>", "application/soap+xml"));

        WebApiResult<XElement> result = await rig.Client.InvokeRawAsync("urn:A", new XElement("A"));

        Assert.True(result.IsSuccess);
        StubRequest sent = rig.Handler.Requests.Single();
        Assert.Equal("application/soap+xml; charset=utf-8; action=\"urn:A\"", sent.ContentHeaders["Content-Type"]);
        Assert.False(sent.Headers.ContainsKey("SOAPAction"));
    }

    [Fact]
    public async Task DefaultNamespace_IsAppliedToTypedBodies()
    {
        await using var rig = new SoapRig(c => c.DefaultNamespace = Ns, script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));

        await rig.Client.InvokeAsync<Ping, XElement>("urn:Ping", new Ping { Seq = 3 });

        Assert.Contains("<Ping xmlns=\"http://mes.local/ns\"><Seq>3</Seq></Ping>", Encoding.UTF8.GetString(rig.Handler.Requests.Single().Body));
    }

    [Fact]
    public async Task SoapHeaders_AndHttpHeaders_AreSent()
    {
        await using var rig = new SoapRig(script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));
        var options = new SoapCallOptions
        {
            SoapHeaders = new List<XElement> { new XElement("Auth", "t") },
            Headers = new Dictionary<string, string> { ["X-Trace"] = "t-1" },
        };

        await rig.Client.InvokeRawAsync("urn:A", new XElement("A"), options);

        StubRequest sent = rig.Handler.Requests.Single();
        Assert.Contains("<soap:Header><Auth>t</Auth></soap:Header>", Encoding.UTF8.GetString(sent.Body));
        Assert.Equal("t-1", sent.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Fault_IsARemoteError_AndIsNeverRetried_EvenWithAnIdempotencyKey()
    {
        await using var rig = new SoapRig(c => c.Retry.RetryCount = 3,
                                          script: handler => handler.Enqueue(500, ServerFault, "text/xml").Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));

        WebApiResult<XElement> result = await rig.Client.InvokeRawAsync("urn:A", new XElement("A"), new SoapCallOptions { IdempotencyKey = "key-1" });

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("soap:Server", result.RemoteCode);
        Assert.Equal("station locked", result.ErrorMessage);
        Assert.Equal(500, result.StatusCode);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task ServerError503_IsRetriedOnlyWhenTheCallHasAnIdempotencyKey()
    {
        await using var withoutKey = new SoapRig(c => c.Retry.RetryCount = 1,
                                                 script: handler => handler.Enqueue(503).Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));
        await using var withKey = new SoapRig(c => c.Retry.RetryCount = 1,
                                              script: handler => handler.Enqueue(503).Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));

        WebApiResult<XElement> plain = await withoutKey.Client.InvokeRawAsync("urn:A", new XElement("A"));
        WebApiResult<XElement> keyed = await withKey.Client.InvokeRawAsync("urn:A", new XElement("A"), new SoapCallOptions { IdempotencyKey = "k" });

        Assert.False(plain.IsSuccess);
        Assert.Single(withoutKey.Handler.Requests);
        Assert.True(keyed.IsSuccess);
        Assert.Equal(2, withKey.Handler.Requests.Count);
        Assert.All(withKey.Handler.Requests, request => Assert.Equal("k", request.Headers["Idempotency-Key"]));
    }

    [Fact]
    public async Task NonXmlSuccessResponse_IsAProtocolViolation()
    {
        await using var rig = new SoapRig(script: handler => handler.Enqueue(200, "<html>login page</html>", "text/html"));

        WebApiResult<XElement> result = await rig.Client.InvokeRawAsync("urn:A", new XElement("A"));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Theory]
    [InlineData("null action")]
    [InlineData("null raw body")]
    [InlineData("zero timeout")]
    public async Task InvalidCall_FailsAsInvalidRequest_WithOneCompletedEventAndNoHttpTraffic(string scenario)
    {
        await using var rig = new SoapRig(script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));

        WebApiResult<XElement> result = scenario switch
        {
            "null action" => await rig.Client.InvokeRawAsync(null!, new XElement("A")),
            "null raw body" => await rig.Client.InvokeRawAsync("urn:A", null!),
            _ => await rig.Client.InvokeRawAsync("urn:A", new XElement("A"), new SoapCallOptions { Timeout = 0 }),
        };

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(rig.Handler.Requests);
        WebApiCompletedEventArgs completed = Assert.Single(rig.Completed);
        Assert.Equal(0, completed.Attempts);
        Assert.Equal("POST", completed.Method);
    }

    [Fact]
    public async Task InvokeAsync_WithANullTypedBody_FailsAsInvalidRequest()
    {
        await using var rig = new SoapRig();

        WebApiResult<XElement> result = await rig.Client.InvokeAsync<GetOrder, XElement>("urn:A", null!);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Single(rig.Completed);
    }

    [Fact]
    public async Task Events_AreForwardedWithTheClientAsSender_AndCanBeUnsubscribed()
    {
        await using var rig = new SoapRig(script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml").Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));
        var senders = new List<object?>();
        EventHandler<WebApiCompletedEventArgs> removed = (sender, args) => senders.Add("removed");
        rig.Client.RequestSending += (sender, args) => senders.Add(sender);
        rig.Client.ResponseReceived += (sender, args) => senders.Add(sender);
        rig.Client.RequestCompleted += (sender, args) => senders.Add(sender);
        rig.Client.RequestCompleted += removed;
        rig.Client.RequestCompleted -= removed;

        await rig.Client.InvokeRawAsync("urn:A", new XElement("A"));

        Assert.Equal(3, senders.Count);
        Assert.All(senders, sender => Assert.Same(rig.Client, sender));
    }

    [Fact]
    public async Task AvailabilityChanged_IsForwardedToTheClientsSubscribers()
    {
        var raised = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new SoapRig(c => c.OfflineThreshold = 1, script: handler => handler.EnqueueException(new HttpRequestException("reset")));
        rig.Client.AvailabilityChanged += (sender, args) => raised.TrySetResult(sender);

        await rig.Client.InvokeRawAsync("urn:A", new XElement("A"));

        Assert.Same(rig.Client, await raised.Task.WaitAsyncCompat(WaitLimit));
        Assert.Equal(WebApiAvailability.Offline, rig.Client.Availability);
    }

    [Fact]
    public async Task HealthCheckUrl_StartsProbingImmediately()
    {
        var probed = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new SoapRig(c =>
        {
            c.HealthCheckUrl = "http://mes.local/service.asmx?wsdl";
            c.HealthCheckInterval = 600000;
        }, script: handler => handler.EnqueueAsync((request, token) =>
        {
            probed.TrySetResult(request.Uri);
            return Task.FromResult(new HttpResponseMessage());
        }));

        Uri address = await probed.Task.WaitAsyncCompat(WaitLimit);

        Assert.Equal("http://mes.local/service.asmx?wsdl", address.AbsoluteUri);
    }

    [Fact]
    public async Task Dispose_ThenInvoke_ReturnsNotConnectedWithoutEvents_AndDisposeIsIdempotent()
    {
        var rig = new SoapRig(script: handler => handler.Enqueue(200, SoapRig.Soap11Response("<R />"), "text/xml"));
        rig.Client.Dispose();
        await rig.DisposeAsync();

        WebApiResult<XElement> result = await rig.Client.InvokeRawAsync("urn:A", new XElement("A"));
        WebApiResult<XElement> invalid = await rig.Client.InvokeRawAsync(null!, new XElement("A"));

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.Equal(CommErrorKind.NotConnected, invalid.ErrorKind);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Handler.Requests);
        Assert.False(rig.Handler.IsDisposed);
    }

    [Fact]
    public async Task PerCallTimeout_OverridesTheConfiguredOne()
    {
        await using var rig = new SoapRig(c => c.Timeout = 600000, script: handler => handler.EnqueueHang());

        WebApiResult<XElement> result = await rig.Client.InvokeRawAsync("urn:A", new XElement("A"), new SoapCallOptions { Timeout = 100 });

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
    }

    private sealed class SystemTextJsonSerializerStub : IWebApiSerializer
    {
        public string ContentType => "application/json";

        public byte[] Serialize(object value, Type type) => Array.Empty<byte>();

        public object? Deserialize(byte[] data, Type type) => null;
    }
}
