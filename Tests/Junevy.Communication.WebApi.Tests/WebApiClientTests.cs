using System.Net.Http;
using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Rest;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiClient"/> 的端到端测试（用 <see cref="StubHttpHandler"/>，不开套接字）：构造、名称、请求线路格式、包络与重试的配合、
/// 事件转发、健康检查路径与释放。解码与请求构造的细节见 <c>RestResponseDecoderTests</c> / <c>RestRequestBuilderTests</c>。
/// </summary>
public sealed class WebApiClientTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Name_DefaultsToTheAuthorityOfTheBaseUrl()
    {
        await using var plain = new RestRig(name: null);
        await using var withPort = new RestRig(c => c.BaseUrl = "https://mes.local:8443/api", name: null);

        Assert.Equal("mes.local", plain.Client.Name);
        Assert.Equal("mes.local:8443", withPort.Client.Name);
    }

    [Fact]
    public async Task Name_CanBeGivenExplicitly_AndAppearsInTheEvents()
    {
        await using var rig = new RestRig(name: "line-3", script: handler => handler.Enqueue(200, "{}"));

        await rig.Client.SendAsync<string>(new WebApiRequest { Path = "ping" });

        Assert.Equal("line-3", rig.Client.Name);
        Assert.Equal("line-3", rig.Completed.Single().EndpointName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ExplicitName_MustNotBeBlank(string name)
    {
        Assert.Throws<ArgumentException>(() => new RestRig(name: name));
    }

    [Fact]
    public void Constructor_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WebApiClient(null!));
        Assert.Throws<ArgumentNullException>(() => new WebApiClient("mes", null!));
    }

    [Fact]
    public void Constructor_InvalidConfig_ThrowsArgumentExceptionNamingTheField()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RestRig(c => c.BaseUrl = "not a url"));

        Assert.Equal("BaseUrl", exception.ParamName);
    }

    [Fact]
    public async Task Config_ReturnsTheObjectGivenToTheConstructor_AndLaterChangesDoNotAffectTheClient()
    {
        WebApiClientConfig? given = null;
        await using var rig = new RestRig(c => given = c, script: handler => handler.Enqueue(200, "{}"));

        given!.BaseUrl = "http://elsewhere.local/";
        await rig.Client.SendAsync<string>(new WebApiRequest { Path = "ping" });

        Assert.Same(given, rig.Client.Config);
        Assert.Equal("http://mes.local/api/ping", rig.Handler.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Send_PutsTheRequestOnTheWire_JsonWithUnescapedChinese()
    {
        await using var rig = new RestRig(c => c.DefaultHeaders["X-Line"] = "L3",
                                          script: handler => handler.Enqueue(200, "{\"orderNo\":\"工单\",\"quantity\":2}"));
        var request = new WebApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/orders",
            Query = new Dictionary<string, string?> { ["station"] = "S 1", ["skip"] = null },
            Body = new { OrderNo = "工单", Quantity = 2 },
            Headers = new Dictionary<string, string> { ["X-Trace"] = "t-1" },
            IdempotencyKey = "key-1",
        };

        WebApiResult<Order> result = await rig.Client.SendAsync<Order>(request);

        Assert.True(result.IsSuccess);
        Assert.Equal("工单", result.Data!.OrderNo);
        StubRequest sent = Assert.Single(rig.Handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("http://mes.local/api/orders?station=S%201", sent.Uri.AbsoluteUri);
        Assert.Equal("L3", sent.Headers["X-Line"]);
        Assert.Equal("t-1", sent.Headers["X-Trace"]);
        Assert.Equal("key-1", sent.Headers["Idempotency-Key"]);
        Assert.StartsWith("application/json", sent.ContentHeaders["Content-Type"]);
        string body = Encoding.UTF8.GetString(sent.Body);
        Assert.Equal("{\"orderNo\":\"工单\",\"quantity\":2}", body);
        Assert.DoesNotContain("\\u", body);
    }

    [Fact]
    public async Task Send_RaisesTheEventsWithTheClientAsSender()
    {
        await using var rig = new RestRig(script: handler => handler.Enqueue(200, "{}"));
        var senders = new List<object?>();
        rig.Client.RequestSending += (sender, args) => senders.Add(sender);
        rig.Client.ResponseReceived += (sender, args) => senders.Add(sender);
        rig.Client.RequestCompleted += (sender, args) => senders.Add(sender);

        await rig.Client.SendAsync<string>(new WebApiRequest { Path = "ping" });

        Assert.Equal(3, senders.Count);
        Assert.All(senders, sender => Assert.Same(rig.Client, sender));
    }

    [Fact]
    public async Task Events_CanBeUnsubscribed()
    {
        await using var rig = new RestRig(script: handler => handler.Enqueue(200, "{}"));
        int calls = 0;
        EventHandler<WebApiCompletedEventArgs> handlerFn = (sender, args) => calls++;
        rig.Client.RequestCompleted += handlerFn;
        rig.Client.RequestCompleted -= handlerFn;

        await rig.Client.SendAsync<string>(new WebApiRequest { Path = "ping" });

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AvailabilityChanged_IsForwardedToTheClientsSubscribers()
    {
        var raised = new TaskCompletionSource<(object? Sender, WebApiAvailabilityChangedEventArgs Args)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new RestRig(c => c.OfflineThreshold = 1, script: handler => handler.EnqueueException(new HttpRequestException("reset")));
        rig.Client.AvailabilityChanged += (sender, args) => raised.TrySetResult((sender, args));

        await rig.Client.SendAsync<string>(new WebApiRequest { Path = "ping" });
        (object? sender, WebApiAvailabilityChangedEventArgs args) = await raised.Task.WaitAsyncCompat(WaitLimit);

        Assert.Same(rig.Client, sender);
        Assert.Equal(WebApiAvailability.Offline, args.CurrentAvailability);
        Assert.Equal(WebApiAvailability.Offline, rig.Client.Availability);
    }

    [Theory]
    [InlineData("request is null")]
    [InlineData("absolute path")]
    [InlineData("byte body without content type")]
    [InlineData("zero timeout")]
    public async Task InvalidRequest_FailsAsInvalidRequest_WithOneCompletedEventAndNoHttpTraffic(string scenario)
    {
        await using var rig = new RestRig(script: handler => handler.Enqueue(200, "{}"));
        WebApiRequest? request = scenario switch
        {
            "request is null" => null,
            "absolute path" => new WebApiRequest { Path = "http://evil.local/x" },
            "byte body without content type" => new WebApiRequest { Method = HttpMethod.Post, Body = new byte[] { 1 } },
            _ => new WebApiRequest { Timeout = 0 },
        };

        WebApiResult<string> result = await rig.Client.SendAsync<string>(request!);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(rig.Handler.Requests);
        Assert.Empty(rig.Sending);
        WebApiCompletedEventArgs completed = Assert.Single(rig.Completed);
        Assert.Equal(0, completed.Attempts);
        Assert.Equal(CommErrorKind.InvalidRequest, completed.ErrorKind);
    }

    [Fact]
    public async Task BusinessFailureInTheEnvelope_IsARemoteErrorAndIsNeverRetried()
    {
        await using var rig = new RestRig(c =>
        {
            c.Retry.RetryCount = 2;
            c.Envelope = new ResponseEnvelopeOptions { Enabled = true };
        }, script: handler => handler.Enqueue(200, "{\"code\":1001,\"msg\":\"duplicate\"}").Enqueue(200, "{\"code\":0}"));

        WebApiResult<Order> result = await rig.Client.SendAsync<Order>(new WebApiRequest { Path = "report" });

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("1001", result.RemoteCode);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task EnvelopeSuccess_ReturnsTheDataAcrossARetry()
    {
        await using var rig = new RestRig(c =>
        {
            c.Retry.RetryCount = 1;
            c.Envelope = new ResponseEnvelopeOptions { Enabled = true };
        }, script: handler => handler.Enqueue(503).Enqueue(200, "{\"code\":0,\"data\":{\"orderNo\":\"工单\"}}"));

        WebApiResult<Order> result = await rig.Client.SendAsync<Order>(new WebApiRequest { Path = "orders/1" });

        Assert.True(result.IsSuccess);
        Assert.Equal("工单", result.Data!.OrderNo);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task Components_Serializer_ReplacesTheDefaultOne()
    {
        await using var rig = new RestRig(configureComponents: components => components.Serializer = new MarkerSerializer(),
                                          script: handler => handler.Enqueue(200, "response"));

        WebApiResult<Order> result = await rig.Client.SendAsync<Order>(new WebApiRequest { Method = HttpMethod.Post, Path = "x", Body = new { A = 1 } });

        StubRequest sent = rig.Handler.Requests.Single();
        Assert.Equal("marker-body", Encoding.UTF8.GetString(sent.Body));
        Assert.Equal("application/x-marker", sent.ContentHeaders["Content-Type"]);
        Assert.Equal("from-marker", result.Data!.OrderNo);
    }

    [Fact]
    public async Task HealthCheckPath_StartsProbingTheResolvedAddressImmediately()
    {
        var probed = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new RestRig(c =>
        {
            c.HealthCheckPath = "health";
            c.HealthCheckInterval = 600000;
        }, script: handler => handler.EnqueueAsync((request, token) =>
        {
            probed.TrySetResult(request.Uri);
            return Task.FromResult(new HttpResponseMessage());
        }));

        Uri address = await probed.Task.WaitAsyncCompat(WaitLimit);

        Assert.Equal("http://mes.local/api/health", address.AbsoluteUri);
    }

    [Fact]
    public async Task Dispose_ThenSend_ReturnsNotConnectedWithoutEvents()
    {
        var rig = new RestRig(script: handler => handler.Enqueue(200, "{}"));
        await rig.DisposeAsync();

        WebApiResult<string> result = await rig.Client.SendAsync<string>(new WebApiRequest { Path = "ping" });
        WebApiResult<string> invalid = await rig.Client.SendAsync<string>(null!);

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.Equal(CommErrorKind.NotConnected, invalid.ErrorKind);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndDoesNotDisposeTheInjectedHandler()
    {
        var rig = new RestRig();

        rig.Client.Dispose();
        rig.Client.Dispose();
        await rig.DisposeAsync();

        Assert.False(rig.Handler.IsDisposed);
    }

    [Fact]
    public async Task Send_HonoursThePerRequestTimeout()
    {
        await using var rig = new RestRig(c => c.Timeout = 600000, script: handler => handler.EnqueueHang());

        WebApiResult<string> result = await rig.Client.SendAsync<string>(new WebApiRequest { Path = "slow", Timeout = 100 });

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
    }

    public sealed class Order
    {
        public string? OrderNo { get; set; }
    }

    private sealed class MarkerSerializer : IWebApiSerializer
    {
        public string ContentType => "application/x-marker";

        public byte[] Serialize(object value, Type type) => Encoding.UTF8.GetBytes("marker-body");

        public object? Deserialize(byte[] data, Type type) => new Order { OrderNo = "from-marker" };
    }
}
