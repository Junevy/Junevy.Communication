using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 拦截器契约与两个上下文的测试：上下文只记录意图（Fail / RequestReplay / MarkSensitiveHeader），由尝试循环读取（P3-D3）。
/// </summary>
public sealed class InterceptorContextTests
{
    private static readonly Uri Origin = new Uri(TestContexts.DefaultUri);

    [Fact]
    public void SendingContext_StoresEveryConstructorArgument()
    {
        var items = new Dictionary<string, object?>();
        var body = new byte[] { 1, 2, 3 };

        var context = new WebApiSendingContext("mes", 7, 2, "PUT", new Uri("http://mes.local/a?b=1"),
                                               new Dictionary<string, string> { ["X-A"] = "1" }, body, "text/plain", "key-1", items);

        Assert.Equal("mes", context.EndpointName);
        Assert.Equal(7, context.CallId);
        Assert.Equal(2, context.Attempt);
        Assert.Equal("PUT", context.Method);
        Assert.Equal(new Uri("http://mes.local/a?b=1"), context.RequestUri);
        Assert.Equal("1", context.Headers["X-A"]);
        Assert.Same(body, context.Body);
        Assert.Equal("text/plain", context.ContentType);
        Assert.Equal("key-1", context.IdempotencyKey);
        Assert.Same(items, context.Items);
    }

    [Fact]
    public void SendingContext_Headers_AreCaseInsensitiveAndIsolatedFromTheSuppliedDictionary()
    {
        var supplied = new Dictionary<string, string> { ["X-Trace"] = "1" };
        var context = TestContexts.Sending(headers: supplied);

        context.Headers["x-trace"] = "2";

        Assert.Single(context.Headers);
        Assert.Equal("1", supplied["X-Trace"]);
    }

    [Fact]
    public void SendingContext_MutableMembers_CanBeReplaced()
    {
        var context = TestContexts.Sending();
        var newBody = new byte[] { 9 };

        context.RequestUri = new Uri("http://mes.local/signed?sig=abc");
        context.Body = newBody;
        context.ContentType = "application/octet-stream";

        Assert.Equal("http://mes.local/signed?sig=abc", context.RequestUri.AbsoluteUri);
        Assert.Same(newBody, context.Body);
        Assert.Equal("application/octet-stream", context.ContentType);
    }

    [Fact]
    public void SendingContext_NullBodyOrUri_Throws()
    {
        var context = TestContexts.Sending();

        Assert.Throws<ArgumentNullException>(() => context.Body = null!);
        Assert.Throws<ArgumentNullException>(() => context.RequestUri = null!);
    }

    [Fact]
    public void SendingContext_NullConstructorArguments_Throw()
    {
        var headers = new Dictionary<string, string>();
        var items = new Dictionary<string, object?>();
        byte[] body = Array.Empty<byte>();

        Assert.Throws<ArgumentNullException>(() => new WebApiSendingContext(null!, 1, 1, "GET", Origin, headers, body, null, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingContext("mes", 1, 1, null!, Origin, headers, body, null, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingContext("mes", 1, 1, "GET", null!, headers, body, null, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingContext("mes", 1, 1, "GET", Origin, null!, body, null, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingContext("mes", 1, 1, "GET", Origin, headers, null!, null, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingContext("mes", 1, 1, "GET", Origin, headers, body, null, null, null!));
    }

    [Fact]
    public void ReceivedContext_NullConstructorArguments_Throw()
    {
        var headers = new Dictionary<string, string>();
        var items = new Dictionary<string, object?>();
        byte[] body = Array.Empty<byte>();

        Assert.Throws<ArgumentNullException>(() => new WebApiReceivedContext(null!, 1, 1, "GET", Origin, 200, headers, body, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiReceivedContext("mes", 1, 1, null!, Origin, 200, headers, body, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiReceivedContext("mes", 1, 1, "GET", null!, 200, headers, body, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiReceivedContext("mes", 1, 1, "GET", Origin, 200, null!, body, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiReceivedContext("mes", 1, 1, "GET", Origin, 200, headers, null!, null, items));
        Assert.Throws<ArgumentNullException>(() => new WebApiReceivedContext("mes", 1, 1, "GET", Origin, 200, headers, body, null, null!));
    }

    [Fact]
    public void SendingContext_Fail_RecordsTheFirstIntentOnly()
    {
        var context = TestContexts.Sending();
        Assert.Null(context.Failure);

        context.Fail("no token available", CommErrorKind.AuthenticationFailed);
        context.Fail("second", CommErrorKind.Timeout);

        Assert.NotNull(context.Failure);
        Assert.Equal("no token available", context.Failure!.Message);
        Assert.Equal(CommErrorKind.AuthenticationFailed, context.Failure.Kind);
    }

    [Fact]
    public void SendingContext_Fail_ValidatesItsArguments()
    {
        var context = TestContexts.Sending();

        Assert.Throws<ArgumentNullException>(() => context.Fail(null!, CommErrorKind.Timeout));
        Assert.Throws<ArgumentException>(() => context.Fail("message", CommErrorKind.None));
        Assert.Null(context.Failure);
    }

    [Fact]
    public void SendingContext_MarkSensitiveHeader_IsRecordedCaseInsensitively()
    {
        var context = TestContexts.Sending();

        context.MarkSensitiveHeader("X-Signature");
        context.MarkSensitiveHeader("x-signature");

        Assert.Contains("x-signature", context.SensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.Single(context.SensitiveHeaders);
        Assert.Throws<ArgumentException>(() => context.MarkSensitiveHeader(""));
        Assert.Throws<ArgumentNullException>(() => context.MarkSensitiveHeader(null!));
    }

    [Fact]
    public void ReceivedContext_StoresEveryConstructorArgument()
    {
        var items = new Dictionary<string, object?>();
        var body = new byte[] { 4, 5 };
        var headers = new Dictionary<string, string> { ["Retry-After"] = "5" };

        var context = new WebApiReceivedContext("mes", 3, 2, "POST", new Uri("http://mes.local/a"), 503, headers, body, "text/plain", items);

        Assert.Equal("mes", context.EndpointName);
        Assert.Equal(3, context.CallId);
        Assert.Equal(2, context.Attempt);
        Assert.Equal("POST", context.Method);
        Assert.Equal(new Uri("http://mes.local/a"), context.RequestUri);
        Assert.Equal(503, context.StatusCode);
        Assert.Same(headers, context.ResponseHeaders);
        Assert.Same(body, context.Body);
        Assert.Equal("text/plain", context.ContentType);
        Assert.Same(items, context.Items);
    }

    [Fact]
    public void ReceivedContext_Body_CanBeReplacedButNotWithNull()
    {
        var context = TestContexts.Received();

        context.Body = new byte[] { 1 };

        Assert.Equal(new byte[] { 1 }, context.Body);
        Assert.Throws<ArgumentNullException>(() => context.Body = null!);
    }

    [Fact]
    public void ReceivedContext_RequestReplay_IsRecorded()
    {
        var context = TestContexts.Received(401);
        Assert.False(context.ReplayRequested);

        context.RequestReplay();
        context.RequestReplay();

        Assert.True(context.ReplayRequested);
    }

    [Fact]
    public void ReceivedContext_Fail_RecordsTheFirstIntentAndValidates()
    {
        var context = TestContexts.Received();

        context.Fail("signature mismatch", CommErrorKind.ProtocolViolation);
        context.Fail("second", CommErrorKind.Timeout);

        Assert.Equal("signature mismatch", context.Failure!.Message);
        Assert.Equal(CommErrorKind.ProtocolViolation, context.Failure.Kind);
        Assert.Throws<ArgumentNullException>(() => context.Fail(null!, CommErrorKind.Timeout));
        Assert.Throws<ArgumentException>(() => context.Fail("message", CommErrorKind.None));
    }

    [Fact]
    public async Task InterceptorBase_DefaultMethodsDoNothing()
    {
        IWebApiInterceptor interceptor = new NoOpInterceptor();
        var sending = TestContexts.Sending();
        var received = TestContexts.Received();

        await interceptor.OnSendingAsync(sending, CancellationToken.None);
        await interceptor.OnReceivedAsync(received, CancellationToken.None);

        Assert.Null(sending.Failure);
        Assert.Null(received.Failure);
        Assert.False(received.ReplayRequested);
        Assert.Empty(sending.Headers);
    }

    private sealed class NoOpInterceptor : WebApiInterceptor
    {
    }
}
