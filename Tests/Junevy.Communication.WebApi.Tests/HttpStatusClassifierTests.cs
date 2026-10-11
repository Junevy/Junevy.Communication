using System.Net.Http;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="HttpStatusClassifier"/> 的归类表测试（设计 15.7 与 P3-D13）：状态码与异常各一张表。
/// </summary>
public sealed class HttpStatusClassifierTests
{
    [Theory]
    [InlineData(200, CommErrorKind.None)]
    [InlineData(201, CommErrorKind.None)]
    [InlineData(204, CommErrorKind.None)]
    [InlineData(299, CommErrorKind.None)]
    [InlineData(301, CommErrorKind.RemoteError)]
    [InlineData(302, CommErrorKind.RemoteError)]
    [InlineData(400, CommErrorKind.RemoteError)]
    [InlineData(401, CommErrorKind.AuthenticationFailed)]
    [InlineData(403, CommErrorKind.AuthenticationFailed)]
    [InlineData(404, CommErrorKind.RemoteError)]
    [InlineData(408, CommErrorKind.RemoteError)]
    [InlineData(429, CommErrorKind.ResourceExhausted)]
    [InlineData(500, CommErrorKind.RemoteError)]
    [InlineData(502, CommErrorKind.RemoteError)]
    [InlineData(503, CommErrorKind.RemoteError)]
    [InlineData(504, CommErrorKind.RemoteError)]
    public void Classify_StatusCode(int statusCode, CommErrorKind expected)
    {
        Assert.Equal(expected, HttpStatusClassifier.Classify(statusCode));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(199)]
    public void Classify_InformationalStatusIsNotSuccess(int statusCode)
    {
        Assert.Equal(CommErrorKind.RemoteError, HttpStatusClassifier.Classify(statusCode));
    }

    public static IEnumerable<object[]> NetworkExceptions()
    {
        yield return new object[] { new HttpRequestException("dns failure") };
        yield return new object[] { new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)) };
        yield return new object[] { new IOException("connection reset") };
        yield return new object[] { new SocketException((int)SocketError.ConnectionReset) };
        yield return new object[] { new System.Net.WebException("aborted") };
    }

    [Theory]
    [MemberData(nameof(NetworkExceptions))]
    public void FromException_NetworkLayerFailures_AreConnectionClosed(Exception exception)
    {
        using TimeoutScope scope = TimeoutScope.Start(10000, CancellationToken.None);

        (CommErrorKind kind, string message) = HttpStatusClassifier.FromException(exception, scope);

        Assert.Equal(CommErrorKind.ConnectionClosed, kind);
        Assert.Contains(exception.Message, message);
    }

    [Fact]
    public async Task FromException_AfterTheTimeoutFired_IsTimeoutWhateverTheException()
    {
        using TimeoutScope scope = TimeoutScope.Start(20, CancellationToken.None);
        await WaitForTokenAsync(scope.Token);

        (CommErrorKind kind, _) = HttpStatusClassifier.FromException(new TaskCanceledException(), scope);
        (CommErrorKind ioKind, _) = HttpStatusClassifier.FromException(new IOException("aborted by timeout"), scope);

        Assert.Equal(CommErrorKind.Timeout, kind);
        Assert.Equal(CommErrorKind.Timeout, ioKind);
    }

    [Fact]
    public void FromException_AfterUserCancellation_IsCancelled()
    {
        using var source = new CancellationTokenSource();
        using TimeoutScope scope = TimeoutScope.Start(10000, source.Token);
        source.Cancel();

        (CommErrorKind kind, _) = HttpStatusClassifier.FromException(new OperationCanceledException(), scope);

        Assert.Equal(CommErrorKind.Cancelled, kind);
    }

    [Fact]
    public void FromException_CancellationNotCausedByTheScope_IsATimeoutReportedByTheHttpStack()
    {
        using TimeoutScope scope = TimeoutScope.Start(10000, CancellationToken.None);

        (CommErrorKind kind, _) = HttpStatusClassifier.FromException(new TaskCanceledException(), scope);

        Assert.Equal(CommErrorKind.Timeout, kind);
    }

    [Fact]
    public void FromException_AnythingElse_IsUnspecifiedAndNamesTheType()
    {
        using TimeoutScope scope = TimeoutScope.Start(10000, CancellationToken.None);

        (CommErrorKind kind, string message) = HttpStatusClassifier.FromException(new InvalidOperationException("boom"), scope);

        Assert.Equal(CommErrorKind.Unspecified, kind);
        Assert.Contains(nameof(InvalidOperationException), message);
        Assert.Contains("boom", message);
    }

    private static Task WaitForTokenAsync(CancellationToken token)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => source.TrySetResult(true));
        return source.Task;
    }
}
