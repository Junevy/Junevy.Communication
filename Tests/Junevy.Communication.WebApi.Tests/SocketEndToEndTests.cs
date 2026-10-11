using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Rest;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 真实套接字的端到端测试（计划 Task 10）：<see cref="RawHttpServer"/> 在 127.0.0.1 的随机端口上按脚本写回原始 HTTP/1.1 响应，
/// REST 与 SOAP 客户端用自己创建的处理器（不注入）连接它。覆盖线路格式、连接失败、超时、半截响应、重试、健康检查、重定向与压缩。
/// 耗时断言用区间，等待用信号而不是睡眠。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class SocketEndToEndTests
{
    private const string Ns = SoapTestNamespaces.Mes;
    private const int ShortTimeout = 500;
    private const int TimeoutSlack = 1500;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Rest_PostWithChineseJson_RoundTripsOverARealConnection()
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.Respond(200, "{\"orderNo\":\"工单-1\",\"quantity\":3}");
        await using WebApiClient client = Rest(server);

        WebApiResult<Order> result = await client.PostAsync<Order>("orders", new { OrderNo = "工单-1", Quantity = 3 });

        Assert.True(result.IsSuccess);
        Assert.Equal("工单-1", result.Data!.OrderNo);
        Assert.Equal(3, result.Data.Quantity);
        RawRequest request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/orders", request.Target);
        Assert.StartsWith("application/json", request.Headers["Content-Type"]);
        Assert.Equal("{\"orderNo\":\"工单-1\",\"quantity\":3}", Encoding.UTF8.GetString(request.Body));
        Assert.Equal(Encoding.UTF8.GetByteCount("{\"orderNo\":\"工单-1\",\"quantity\":3}").ToString(), request.Headers["Content-Length"]);
        Assert.False(request.Headers.ContainsKey("Expect"), "The client must not send Expect: 100-continue.");
    }

    [Fact]
    public async Task Rest_GetWithQuery_SendsTheEncodedTarget()
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.Respond(200, "[]");
        await using WebApiClient client = Rest(server);

        await client.GetAsync<string>("search", new Dictionary<string, string?> { ["q"] = "a b&c", ["n"] = "工单" });

        Assert.Equal("/api/search?q=a%20b%26c&n=%E5%B7%A5%E5%8D%95", Assert.Single(server.Requests).Target);
    }

    [Theory]
    [InlineData(SoapVersion.Soap11)]
    [InlineData(SoapVersion.Soap12)]
    public async Task Soap_RoundTripsOverARealConnection(SoapVersion version)
    {
        string envelopeNs = version == SoapVersion.Soap11 ? SoapTestNamespaces.Soap11 : SoapTestNamespaces.Soap12;
        using RawHttpServer server = RawHttpServer.Start();
        server.Respond(200, $"<s:Envelope xmlns:s=\"{envelopeNs}\"><s:Body><GetOrderResponse xmlns=\"{Ns}\"><Status>完成</Status><Quantity>7</Quantity></GetOrderResponse></s:Body></s:Envelope>",
                       version == SoapVersion.Soap11 ? "text/xml; charset=utf-8" : "application/soap+xml; charset=utf-8");
        await using SoapClient client = Soap(server, version);

        WebApiResult<GetOrderResponse> result = await client.InvokeAsync<GetOrder, GetOrderResponse>("urn:GetOrder", new GetOrder { OrderNo = "工单-1" });

        Assert.True(result.IsSuccess);
        Assert.Equal("完成", result.Data!.Status);
        Assert.Equal(7, result.Data.Quantity);
        RawRequest request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/service.asmx", request.Target);
        if (version == SoapVersion.Soap11)
        {
            Assert.Equal("\"urn:GetOrder\"", request.Headers["SOAPAction"]);
            Assert.StartsWith("text/xml", request.Headers["Content-Type"]);
        }
        else
        {
            Assert.StartsWith("application/soap+xml", request.Headers["Content-Type"]);
            Assert.Contains("action=\"urn:GetOrder\"", request.Headers["Content-Type"]);
        }

        Assert.Contains("<OrderNo>工单-1</OrderNo>", Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task Soap_FaultOverARealConnection_IsARemoteErrorAndNotRetried()
    {
        using RawHttpServer server = RawHttpServer.Start();
        string fault = $"<soap:Envelope xmlns:soap=\"{SoapTestNamespaces.Soap11}\"><soap:Body><soap:Fault><faultcode>soap:Server</faultcode><faultstring>locked</faultstring></soap:Fault></soap:Body></soap:Envelope>";
        server.Respond(500, fault, "text/xml; charset=utf-8").Respond(200, SoapRig.Soap11Response("<A />"), "text/xml");
        await using SoapClient client = Soap(server, SoapVersion.Soap11, c => c.Retry.RetryCount = 2);

        WebApiResult<XElement> result = await client.InvokeRawAsync("urn:A", new XElement("A"), new SoapCallOptions { IdempotencyKey = "k" });

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("soap:Server", result.RemoteCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ConnectionRefused_IsConnectionClosed()
    {
        int closedPort = RawHttpServer.ReserveClosedPort();
        await using var client = new WebApiClient(new WebApiClientConfig { BaseUrl = $"http://127.0.0.1:{closedPort}/api", Timeout = 5000 });

        WebApiResult<string> result = await client.GetAsync<string>("orders");

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Null(result.StatusCode);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task ServerThatAcceptsButNeverAnswers_TimesOutWithinTheTimeoutPlusSlack()
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.Silent();
        await using WebApiClient client = Rest(server, c => c.Timeout = ShortTimeout);
        WebApiResult<string>? result = null;

        await TimingAssert.WithinAsync(TimeSpan.FromMilliseconds(ShortTimeout * 0.8), TimeSpan.FromMilliseconds(ShortTimeout + TimeoutSlack),
                                       async () => result = await client.GetAsync<string>("orders"));

        Assert.Equal(CommErrorKind.Timeout, result!.ErrorKind);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ResponseCutOffHalfway_IsConnectionClosed()
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.HalfResponse();
        await using WebApiClient client = Rest(server, c => c.Timeout = 5000);

        WebApiResult<string> result = await client.GetAsync<string>("orders");

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task Get_AfterADroppedConnection_IsRetriedOnANewOne()
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.CloseWithoutResponse().Respond(200, "ok-after-retry", "text/plain; charset=utf-8");
        await using WebApiClient client = Rest(server, c => c.Retry.RetryCount = 1);

        WebApiResult<string> result = await client.GetAsync<string>("orders");

        Assert.True(result.IsSuccess);
        Assert.Equal("ok-after-retry", result.Data);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task HealthCheck_TurnsUnknownIntoOnline_AndOfflineOnceTheServerIsGone()
    {
        RawHttpServer server = RawHttpServer.Start();
        for (int i = 0; i < 100; i++)
            server.Respond(200, "wsdl", "text/xml");
        await using WebApiClient client = Rest(server, c =>
        {
            c.HealthCheckPath = "health";
            c.HealthCheckInterval = 50;
            c.OfflineThreshold = 1;
            c.Timeout = 2000;
        });

        await WaitForAvailabilityAsync(client, WebApiAvailability.Online);
        server.Dispose();
        await WaitForAvailabilityAsync(client, WebApiAvailability.Offline);

        Assert.Equal("/api/health", server.Requests.First().Target);
        Assert.Equal(WebApiAvailability.Offline, client.Availability);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed_AndAPostIsNotTurnedIntoAGet()
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.Respond(302, "", null, new Dictionary<string, string> { ["Location"] = "/elsewhere" }).Respond(200, "should not be requested");
        await using WebApiClient client = Rest(server);

        WebApiResult<string> result = await client.PostAsync<string>("orders", new { A = 1 });

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(302, result.StatusCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task GzipResponse_IsDecompressed()
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.RespondBytes(200, Gzip("{\"orderNo\":\"压缩\"}"), "application/json; charset=utf-8", new Dictionary<string, string> { ["Content-Encoding"] = "gzip" });
        await using WebApiClient client = Rest(server);

        WebApiResult<Order> result = await client.GetAsync<Order>("orders/1");

        Assert.True(result.IsSuccess);
        Assert.Equal("压缩", result.Data!.OrderNo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChunkedResponse_IsReadAndTheBodyLimitIsEnforcedWhileReading(bool overTheLimit)
    {
        using RawHttpServer server = RawHttpServer.Start();
        server.RespondChunked(200, "0123456789ABCDEF", chunkSize: 4);
        await using WebApiClient client = Rest(server, c => c.MaxResponseBodySize = overTheLimit ? 10 : 100);

        WebApiResult<string> result = await client.GetAsync<string>("orders");

        if (overTheLimit)
        {
            Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
            Assert.Equal(200, result.StatusCode);
        }
        else
        {
            Assert.True(result.IsSuccess);
            Assert.Equal("0123456789ABCDEF", result.Data);
        }
    }

    [Fact]
    public async Task ManyCallsOnOneClient_ReuseTheConnectionAndAllSucceed()
    {
        using RawHttpServer server = RawHttpServer.Start();
        for (int i = 0; i < 10; i++)
            server.Respond(200, "ok", "text/plain; charset=utf-8");
        await using WebApiClient client = Rest(server);

        for (int i = 0; i < 10; i++)
            Assert.True((await client.GetAsync<string>("orders")).IsSuccess);

        Assert.Equal(10, server.Requests.Count);
        Assert.Equal(1, server.AcceptedConnections);
    }

    [Fact]
    public async Task DisposeWhileARequestIsInFlight_AlwaysReturnsConnectionClosed_AndLeavesNoUnobservedTaskExceptions()
    {
        var unobserved = new List<string>();
        EventHandler<UnobservedTaskExceptionEventArgs> record = (sender, args) =>
        {
            lock (unobserved)
                unobserved.Add(args.Exception.ToString());
        };
        TaskScheduler.UnobservedTaskException += record;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                using RawHttpServer server = RawHttpServer.Start();
                server.Silent();
                WebApiClient client = Rest(server, c => c.Timeout = 30000);
                Task<WebApiResult<string>> call = client.GetAsync<string>("orders");
                await server.WaitForRequestsAsync(1, WaitLimit);

                await client.DisposeAsync();
                WebApiResult<string> result = await call.WaitAsyncCompat(WaitLimit);

                Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= record;
        }

        lock (unobserved)
            Assert.DoesNotContain(unobserved, text => text.Contains("Junevy.Communication.WebApi"));
    }

    private static WebApiClient Rest(RawHttpServer server, Action<WebApiClientConfig>? configure = null)
    {
        var config = new WebApiClientConfig { BaseUrl = server.BaseUrl + "/api", Retry = new WebApiRetryOptions { RetryDelay = 0 } };
        configure?.Invoke(config);
        return new WebApiClient("real", config);
    }

    private static SoapClient Soap(RawHttpServer server, SoapVersion version, Action<SoapClientConfig>? configure = null)
    {
        var config = new SoapClientConfig { Url = server.BaseUrl + "/service.asmx", Version = version, Retry = new WebApiRetryOptions { RetryDelay = 0 } };
        configure?.Invoke(config);
        return new SoapClient("real", config);
    }

    // 先订阅再读一次 Availability（IWebApiEndpoint.AvailabilityChanged 文档规定的用法），不轮询。
    private static async Task WaitForAvailabilityAsync(IWebApiEndpoint endpoint, WebApiAvailability expected)
    {
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<WebApiAvailabilityChangedEventArgs> handler = (sender, args) =>
        {
            if (args.CurrentAvailability == expected)
                reached.TrySetResult(true);
        };
        endpoint.AvailabilityChanged += handler;
        try
        {
            if (endpoint.Availability == expected)
                reached.TrySetResult(true);

            await reached.Task.WaitAsyncCompat(WaitLimit);
        }
        finally
        {
            endpoint.AvailabilityChanged -= handler;
        }
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    public sealed class Order
    {
        public string? OrderNo { get; set; }

        public int Quantity { get; set; }
    }
}
