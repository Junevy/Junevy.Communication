using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 事件参数类型的构造函数测试：全部公开并保存全部参数（P2.5 先例：自定义实现也能引发事件）。
/// </summary>
public sealed class EventArgsTests
{
    private static readonly Uri RequestUri = new Uri("http://mes.local/api?token=***");

    [Fact]
    public void SendingEventArgs_StoresEveryArgument()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "***" };
        var body = new byte[] { 1, 2 };

        var args = new WebApiSendingEventArgs("mes", 5, "POST", RequestUri, 2, headers, body, "application/json");

        Assert.Equal("mes", args.EndpointName);
        Assert.Equal(5, args.CallId);
        Assert.Equal("POST", args.Method);
        Assert.Equal(RequestUri, args.RequestUri);
        Assert.Equal(2, args.Attempt);
        Assert.Same(headers, args.Headers);
        Assert.Equal(body, args.Body.ToArray());
        Assert.Equal("application/json", args.ContentType);
    }

    [Fact]
    public void ResponseEventArgs_StoresEveryArgument()
    {
        var exception = new InvalidOperationException("inner");
        var body = new byte[] { 3 };

        var args = new WebApiResponseEventArgs("mes", 5, "GET", RequestUri, 3, 503, CommErrorKind.RemoteError, body,
                                               TimeSpan.FromMilliseconds(42), true, 1000, exception);

        Assert.Equal("mes", args.EndpointName);
        Assert.Equal(5, args.CallId);
        Assert.Equal("GET", args.Method);
        Assert.Equal(RequestUri, args.RequestUri);
        Assert.Equal(3, args.Attempt);
        Assert.Equal(503, args.StatusCode);
        Assert.Equal(CommErrorKind.RemoteError, args.ErrorKind);
        Assert.Equal(body, args.Body.ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(42), args.Elapsed);
        Assert.True(args.WillRetry);
        Assert.Equal(1000, args.RetryDelay);
        Assert.Same(exception, args.Exception);
    }

    [Fact]
    public void ResponseEventArgs_NetworkFailure_HasNoStatusCode()
    {
        var args = new WebApiResponseEventArgs("mes", 1, "GET", RequestUri, 1, null, CommErrorKind.ConnectionClosed, ReadOnlyMemory<byte>.Empty,
                                               TimeSpan.Zero, false, 0, null);

        Assert.Null(args.StatusCode);
        Assert.True(args.Body.IsEmpty);
        Assert.Null(args.Exception);
    }

    [Fact]
    public void CompletedEventArgs_StoresEveryArgument()
    {
        var args = new WebApiCompletedEventArgs("mes", 5, "POST", RequestUri, 2, false, 500, "E42", CommErrorKind.RemoteError, "rejected",
                                                TimeSpan.FromSeconds(1));

        Assert.Equal("mes", args.EndpointName);
        Assert.Equal(5, args.CallId);
        Assert.Equal("POST", args.Method);
        Assert.Equal(RequestUri, args.RequestUri);
        Assert.Equal(2, args.Attempts);
        Assert.False(args.IsSuccess);
        Assert.Equal(500, args.StatusCode);
        Assert.Equal("E42", args.RemoteCode);
        Assert.Equal(CommErrorKind.RemoteError, args.ErrorKind);
        Assert.Equal("rejected", args.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(1), args.Elapsed);
    }

    [Fact]
    public void AvailabilityChangedEventArgs_StoresEveryArgument()
    {
        var exception = new InvalidOperationException("inner");

        var args = new WebApiAvailabilityChangedEventArgs("mes", WebApiAvailability.Online, WebApiAvailability.Offline, CommErrorKind.Timeout, exception);

        Assert.Equal("mes", args.EndpointName);
        Assert.Equal(WebApiAvailability.Online, args.PreviousAvailability);
        Assert.Equal(WebApiAvailability.Offline, args.CurrentAvailability);
        Assert.Equal(CommErrorKind.Timeout, args.Reason);
        Assert.Same(exception, args.Exception);
    }

    [Fact]
    public void EventArgs_NullArguments_Throw()
    {
        var headers = new Dictionary<string, string>();

        Assert.Throws<ArgumentNullException>(() => new WebApiSendingEventArgs(null!, 1, "GET", RequestUri, 1, headers, ReadOnlyMemory<byte>.Empty, null));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingEventArgs("mes", 1, null!, RequestUri, 1, headers, ReadOnlyMemory<byte>.Empty, null));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingEventArgs("mes", 1, "GET", null!, 1, headers, ReadOnlyMemory<byte>.Empty, null));
        Assert.Throws<ArgumentNullException>(() => new WebApiSendingEventArgs("mes", 1, "GET", RequestUri, 1, null!, ReadOnlyMemory<byte>.Empty, null));
        Assert.Throws<ArgumentNullException>(() => new WebApiAvailabilityChangedEventArgs(null!, WebApiAvailability.Unknown, WebApiAvailability.Online, CommErrorKind.None, null));
    }
}
