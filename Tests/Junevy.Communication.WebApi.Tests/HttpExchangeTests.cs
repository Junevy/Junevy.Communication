using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="HttpExchange"/> 的单次尝试测试：请求转换（内容头路由、ExpectContinue）、响应读取（正文上限）与异常归类（P3-D11 / D13）。
/// 全部使用 <see cref="StubHttpHandler"/>，不开套接字。
/// </summary>
public sealed class HttpExchangeTests
{
    private const int DefaultLimit = 1024;
    private const int GenerousTimeout = 10000;

    [Fact]
    public async Task Send_Success_ReturnsStatusHeadersBodyAndContentType()
    {
        var handler = new StubHttpHandler().Enqueue(201, "{\"ok\":true}", "application/json; charset=utf-8", new Dictionary<string, string> { ["X-Reply"] = "yes" });

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending());

        HttpExchangeResponse response = outcome.Response!;
        Assert.NotNull(response);
        Assert.Equal(201, response.StatusCode);
        Assert.Equal("Created", response.ReasonPhrase);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(response.Body));
        Assert.StartsWith("application/json", response.ContentType);
        Assert.Equal("yes", response.Headers["x-reply"]);
        Assert.Equal(201, outcome.StatusCode);
    }

    [Fact]
    public async Task Send_NonSuccessStatus_IsStillAResponseNotAFailure()
    {
        var handler = new StubHttpHandler().Enqueue(503, "busy");

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending());

        Assert.NotNull(outcome.Response);
        Assert.Equal(503, outcome.Response!.StatusCode);
        Assert.Equal(CommErrorKind.None, outcome.FailureKind);
    }

    [Fact]
    public async Task Send_ForwardsMethodUriAndBody()
    {
        var handler = new StubHttpHandler().Enqueue(200);
        byte[] body = Encoding.UTF8.GetBytes("{\"order\":\"工单\"}");
        var context = TestContexts.Sending(method: "PUT", uri: "http://mes.local/api/orders?id=7", body: body);

        await SendAsync(handler, context);

        StubRequest request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("http://mes.local/api/orders?id=7", request.Uri.AbsoluteUri);
        Assert.Equal(body, request.Body);
    }

    [Fact]
    public async Task Send_RoutesContentHeadersToTheContentAndCustomHeadersToTheRequest()
    {
        var handler = new StubHttpHandler().Enqueue(200);
        var headers = new Dictionary<string, string> { ["X-Custom"] = "1", ["Content-Language"] = "zh-CN" };
        var context = TestContexts.Sending(headers: headers, body: new byte[] { 1 }, contentType: "application/json");

        await SendAsync(handler, context);

        StubRequest request = Assert.Single(handler.Requests);
        Assert.Equal("1", request.Headers["X-Custom"]);
        Assert.False(request.Headers.ContainsKey("Content-Language"));
        Assert.False(request.Headers.ContainsKey("Content-Type"));
        Assert.Equal("zh-CN", request.ContentHeaders["Content-Language"]);
        Assert.StartsWith("application/json", request.ContentHeaders["Content-Type"]);
    }

    [Fact]
    public async Task Send_ContentTypeProperty_ReplacesAContentTypeHeader()
    {
        var handler = new StubHttpHandler().Enqueue(200);
        var headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain" };
        var context = TestContexts.Sending(headers: headers, body: new byte[] { 1 }, contentType: "application/xml");

        await SendAsync(handler, context);

        StubRequest request = Assert.Single(handler.Requests);
        Assert.Equal("application/xml", request.ContentHeaders["Content-Type"]);
    }

    [Fact]
    public async Task Send_DisablesExpectContinue()
    {
        var handler = new StubHttpHandler().Enqueue(200);

        await SendAsync(handler, TestContexts.Sending(body: new byte[] { 1 }));

        Assert.Equal(false, Assert.Single(handler.Requests).ExpectContinue);
    }

    [Fact]
    public async Task Send_WithoutBodyOrContentType_SendsNoContent()
    {
        var handler = new StubHttpHandler().Enqueue(200);

        await SendAsync(handler, TestContexts.Sending(method: "GET", contentType: null));

        Assert.False(Assert.Single(handler.Requests).HasContent);
    }

    [Fact]
    public async Task Send_InvalidHeaderName_FailsAsInvalidRequestWithoutSending()
    {
        var handler = new StubHttpHandler();
        var context = TestContexts.Sending(method: "GET", contentType: null, headers: new Dictionary<string, string> { ["Bad Name"] = "x" });

        HttpExchangeOutcome outcome = await SendAsync(handler, context);

        Assert.Equal(CommErrorKind.InvalidRequest, outcome.FailureKind);
        Assert.Contains("Bad Name", outcome.FailureMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Send_BodyAtTheLimit_IsAccepted()
    {
        var handler = new StubHttpHandler().EnqueueBytes(200, new byte[10]);

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending(), maxBody: 10);

        Assert.Equal(10, outcome.Response!.Body.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_BodyOverTheLimit_IsAProtocolViolationWithTheStatusCode(bool chunked)
    {
        var handler = new StubHttpHandler();
        if (chunked)
            handler.EnqueueChunked(200, new byte[11]);
        else
            handler.EnqueueBytes(200, new byte[11]);

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending(), maxBody: 10);

        Assert.Null(outcome.Response);
        Assert.Equal(CommErrorKind.ProtocolViolation, outcome.FailureKind);
        Assert.Equal(200, outcome.StatusCode);
        Assert.Contains("10", outcome.FailureMessage);
    }

    [Fact]
    public async Task Send_ChunkedBodyWithinTheLimit_IsReadCompletely()
    {
        byte[] payload = Encoding.UTF8.GetBytes("chunked payload");
        var handler = new StubHttpHandler().EnqueueChunked(200, payload);

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending());

        Assert.Equal(payload, outcome.Response!.Body);
    }

    [Fact]
    public async Task Send_EmptyBody_ReturnsAnEmptyArray()
    {
        var handler = new StubHttpHandler().Enqueue(204, "", null);

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending());

        Assert.Empty(outcome.Response!.Body);
        Assert.Null(outcome.Response.ContentType);
    }

    [Fact]
    public async Task Send_ResponseWithoutAContentObject_IsAnEmptyBody()
    {
        var handler = new StubHttpHandler().EnqueueResponse(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending());

        Assert.Empty(outcome.Response!.Body);
        Assert.Equal(200, outcome.Response.StatusCode);
    }

    [Fact]
    public async Task Send_HandlerNeverResponds_TimesOutAsTimeout()
    {
        var handler = new StubHttpHandler().EnqueueHang();

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending(), timeout: 100);

        Assert.Equal(CommErrorKind.Timeout, outcome.FailureKind);
        Assert.Null(outcome.Response);
    }

    [Fact]
    public async Task Send_ResponseBodyNeverCompletes_TimesOutAsTimeout()
    {
        var handler = new StubHttpHandler().EnqueueResponse(() => new HttpResponseMessage { Content = new StreamContent(new HangingStream()) });

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending(), timeout: 100);

        Assert.Equal(CommErrorKind.Timeout, outcome.FailureKind);
    }

    [Fact]
    public async Task Send_UserCancellation_IsCancelledNotTimeout()
    {
        var handler = new StubHttpHandler().EnqueueHang();
        using var source = new CancellationTokenSource();
        source.CancelAfter(50);

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending(), timeout: GenerousTimeout, userToken: source.Token);

        Assert.Equal(CommErrorKind.Cancelled, outcome.FailureKind);
    }

    [Fact]
    public async Task Send_NetworkFailure_IsConnectionClosedAndKeepsTheException()
    {
        var failure = new HttpRequestException("name resolution failed", new SocketException((int)SocketError.HostNotFound));
        var handler = new StubHttpHandler().EnqueueException(failure);

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending());

        Assert.Equal(CommErrorKind.ConnectionClosed, outcome.FailureKind);
        Assert.Same(failure, outcome.Exception);
        Assert.Null(outcome.StatusCode);
        Assert.Contains("name resolution failed", outcome.FailureMessage);
    }

    [Fact]
    public async Task Send_UnexpectedException_IsUnspecified()
    {
        var handler = new StubHttpHandler().EnqueueException(new InvalidOperationException("boom"));

        HttpExchangeOutcome outcome = await SendAsync(handler, TestContexts.Sending());

        Assert.Equal(CommErrorKind.Unspecified, outcome.FailureKind);
    }

    private static async Task<HttpExchangeOutcome> SendAsync(StubHttpHandler handler, WebApiSendingContext context, int timeout = GenerousTimeout,
                                                             int maxBody = DefaultLimit, CancellationToken userToken = default)
    {
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var exchange = new HttpExchange(client, maxBody);
        using TimeoutScope scope = TimeoutScope.Start(timeout, userToken);
        return await exchange.SendAsync(context, scope);
    }

    /// <summary>读取时一直等到取消的流，用来模拟响应头已到、正文永不结束。</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
