using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Auth;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 尝试循环的事件契约（P3-D8 / D9，计划 Task 6 测试清单"事件"）：典型序列、网络层失败、每次调用恰好一个 Completed、
/// 订阅者异常隔离、脱敏、同步可观察，以及可用性报告与结束日志。
/// </summary>
public sealed class AttemptLoopEventTests
{
    [Fact]
    public async Task TypicalSequence_503ThenSuccess()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        await rig.SendAsync("GET");

        Assert.Equal(new[] { "Sending(1)", "Response(1,503,retry=True)", "Sending(2)", "Response(2,200,retry=False)", "Completed(2,True)" }, rig.Timeline);
    }

    [Fact]
    public async Task ResponseReceived_CarriesTheAttemptDetails()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        rig.Handler.Enqueue(503, "busy").Enqueue(200, "ok");

        await rig.SendAsync("GET");

        WebApiResponseEventArgs first = rig.Responses.First();
        Assert.Equal("mes", first.EndpointName);
        Assert.Equal(1, first.CallId);
        Assert.Equal("GET", first.Method);
        Assert.Equal(1, first.Attempt);
        Assert.Equal(503, first.StatusCode);
        Assert.Equal(CommErrorKind.RemoteError, first.ErrorKind);
        Assert.Equal("busy", System.Text.Encoding.UTF8.GetString(first.Body.ToArray()));
        Assert.True(first.WillRetry);
        Assert.Equal(0, first.RetryDelay);
        Assert.True(first.Elapsed >= TimeSpan.Zero);
        Assert.Equal(CommErrorKind.None, rig.Responses.Last().ErrorKind);
    }

    [Fact]
    public async Task NetworkFailure_RaisesResponseReceivedWithoutAStatusCode()
    {
        using var rig = new LoopRig();
        var failure = new HttpRequestException("connection refused");
        rig.Handler.EnqueueException(failure);

        await rig.SendAsync("GET");

        WebApiResponseEventArgs args = Assert.Single(rig.Responses);
        Assert.Null(args.StatusCode);
        Assert.Equal(CommErrorKind.ConnectionClosed, args.ErrorKind);
        Assert.True(args.Body.IsEmpty);
        Assert.Same(failure, args.Exception);
        Assert.Equal(new[] { "Sending(1)", "Response(1,-,retry=False)", "Completed(1,False)" }, rig.Timeline);
    }

    [Fact]
    public async Task Completed_CarriesTheFinalResult()
    {
        using var rig = new LoopRig();
        rig.Handler.Enqueue(500, "denied", headers: new Dictionary<string, string> { ["X-Remote-Code"] = "E42" });

        await rig.SendAsync("POST", "orders/7", body: new byte[] { 1 }, contentType: "application/json");

        WebApiCompletedEventArgs completed = Assert.Single(rig.Completed);
        Assert.Equal("mes", completed.EndpointName);
        Assert.Equal(1, completed.CallId);
        Assert.Equal("POST", completed.Method);
        Assert.Equal("http://mes.local/api/orders/7", completed.RequestUri.AbsoluteUri);
        Assert.Equal(1, completed.Attempts);
        Assert.False(completed.IsSuccess);
        Assert.Equal(500, completed.StatusCode);
        Assert.Equal("E42", completed.RemoteCode);
        Assert.Equal(CommErrorKind.RemoteError, completed.ErrorKind);
        Assert.Equal("HTTP 500.", completed.ErrorMessage);
        Assert.True(completed.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Completed_OnSuccess_HasNoErrorFields()
    {
        using var rig = new LoopRig();
        rig.Handler.Enqueue(200, "ok");

        await rig.SendAsync("GET");

        WebApiCompletedEventArgs completed = Assert.Single(rig.Completed);
        Assert.True(completed.IsSuccess);
        Assert.Equal(CommErrorKind.None, completed.ErrorKind);
        Assert.Null(completed.ErrorMessage);
        Assert.Null(completed.RemoteCode);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("remote error")]
    [InlineData("network failure")]
    [InlineData("interceptor failure")]
    [InlineData("rejected before any attempt")]
    public async Task EveryCall_RaisesExactlyOneCompleted(string scenario)
    {
        var gate = ScriptedInterceptor.Sending("gate", context =>
        {
            if (scenario == "interceptor failure")
                context.Fail("blocked", CommErrorKind.AuthenticationFailed);
        });
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1, new[] { gate });

        switch (scenario)
        {
            case "success":
                rig.Handler.Enqueue(200);
                break;
            case "remote error":
                rig.Handler.Enqueue(404);
                break;
            case "network failure":
                rig.Handler.EnqueueException(new HttpRequestException("reset")).EnqueueException(new HttpRequestException("reset"));
                break;
        }

        if (scenario == "rejected before any attempt")
            rig.Loop.Reject<string>("POST", "The path must be relative.", CommErrorKind.InvalidRequest);
        else
            await rig.SendAsync("GET");

        Assert.Single(rig.Completed);
        Assert.Single(rig.Timeline, entry => entry.StartsWith("Completed", StringComparison.Ordinal));
        Assert.StartsWith("Completed", rig.Timeline.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reject_RaisesCompletedWithZeroAttemptsAndReturnsTheFailure()
    {
        using var rig = new LoopRig();

        WebApiResult<string> result = rig.Loop.Reject<string>("POST", "The path must be relative.", CommErrorKind.InvalidRequest);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The path must be relative.", result.ErrorMessage);
        WebApiCompletedEventArgs completed = Assert.Single(rig.Completed);
        Assert.Equal(0, completed.Attempts);
        Assert.Equal("POST", completed.Method);
        Assert.Equal(LoopRig.BaseAddress, completed.RequestUri);
        Assert.Equal(new[] { "Completed(0,False)" }, rig.Timeline);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task CallIds_IncreaseFromOnePerCall_IncludingRejectedCalls()
    {
        using var rig = new LoopRig();
        rig.Handler.Enqueue(200).Enqueue(200);

        await rig.SendAsync();
        rig.Loop.Reject<string>("GET", "invalid", CommErrorKind.InvalidRequest);
        await rig.SendAsync();

        Assert.Equal(new long[] { 1, 2, 3 }, rig.Completed.Select(args => args.CallId));
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotAffectTheResultOrTheOtherSubscribers()
    {
        using var rig = new LoopRig();
        var received = new List<string>();
        rig.Events.RequestSending += (sender, args) => throw new InvalidOperationException("subscriber bug");
        rig.Events.RequestCompleted += (sender, args) => throw new InvalidOperationException("subscriber bug");
        rig.Events.RequestCompleted += (sender, args) => received.Add("late subscriber");
        rig.Handler.Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "late subscriber" }, received);
        Assert.Equal(2, rig.Logger.GetEntries(LogLevel.Error).Count);
        Assert.Contains("Completed(1,True)", rig.Timeline);
    }

    [Fact]
    public async Task RequestCompleted_HasAlreadyRunWhenTheCallReturns()
    {
        using var rig = new LoopRig();
        bool handlerRan = false;
        rig.Events.RequestCompleted += (sender, args) => handlerRan = true;
        rig.Handler.Enqueue(200);

        await rig.SendAsync();

        Assert.True(handlerRan);
    }

    [Fact]
    public async Task EventSender_IsTheObjectGivenToTheEventHub()
    {
        using var rig = new LoopRig();
        object? sender = null;
        rig.Events.RequestCompleted += (s, args) => sender = s;
        rig.Handler.Enqueue(200);

        await rig.SendAsync();

        Assert.Same(rig, sender);
    }

    [Fact]
    public async Task SensitiveHeadersAndQueryParameters_AreMaskedInEventArgsButSentIntact()
    {
        var signer = ScriptedInterceptor.Sending("signer", context =>
        {
            context.Headers["X-Signature"] = "sig-value";
            context.MarkSensitiveHeader("X-Signature");
        });
        using var rig = new LoopRig(interceptors: new[] { signer }, authInterceptor: WebApiAuthentication.Bearer("secret-token"));
        rig.Handler.Enqueue(200);

        await rig.SendAsync("GET", "orders?token=abc&page=1", headers: new Dictionary<string, string> { ["X-Trace"] = "visible" });

        WebApiSendingEventArgs args = Assert.Single(rig.Sending);
        Assert.Equal("***", args.Headers["Authorization"]);
        Assert.Equal("***", args.Headers["X-Signature"]);
        Assert.Equal("visible", args.Headers["X-Trace"]);
        Assert.Equal("http://mes.local/api/orders?token=***&page=1", args.RequestUri.AbsoluteUri);
        Assert.Equal("Bearer secret-token", rig.Handler.Requests.Single().Headers["Authorization"]);
        Assert.Equal("sig-value", rig.Handler.Requests.Single().Headers["X-Signature"]);
        Assert.Equal("http://mes.local/api/orders?token=abc&page=1", rig.Handler.Requests.Single().Uri.AbsoluteUri);
        Assert.DoesNotContain(rig.Logger.Entries, entry => entry.Message.Contains("secret-token") || entry.Message.Contains("sig-value"));
        Assert.Equal("http://mes.local/api/orders?token=***&page=1", rig.Completed.Single().RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task SendingEvent_CarriesTheBytesActuallySent()
    {
        using var rig = new LoopRig();
        rig.Handler.Enqueue(200);

        await rig.SendAsync("POST", "orders", body: new byte[] { 1, 2, 3 }, contentType: "application/octet-stream");

        WebApiSendingEventArgs args = rig.Sending.Single();
        Assert.Equal(new byte[] { 1, 2, 3 }, args.Body.ToArray());
        Assert.Equal("application/octet-stream", args.ContentType);
        Assert.Equal("POST", args.Method);
        Assert.Equal(1, args.Attempt);
    }

    [Fact]
    public async Task Availability_ReceivesOneReportPerSentAttempt()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2);
        rig.Handler.Enqueue(503).EnqueueException(new HttpRequestException("reset")).Enqueue(200);

        await rig.SendAsync("GET");

        Assert.Equal(new[] { (CommErrorKind.None, (int?)503), (CommErrorKind.ConnectionClosed, (int?)null), (CommErrorKind.None, (int?)200) },
                     rig.Availability.Reports);
    }

    [Fact]
    public async Task Availability_IsNotToldAboutAttemptsThatWereNeverSent()
    {
        var failing = ScriptedInterceptor.Sending("gate", context => context.Fail("blocked", CommErrorKind.ConnectionClosed));
        using var rig = new LoopRig(interceptors: new[] { failing });

        await rig.SendAsync("GET");

        Assert.Empty(rig.Availability.Reports);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalFailure_IsLoggedAsWarningOnlyForConnectionTimeoutOrServerErrors(bool serious)
    {
        using var rig = new LoopRig();
        if (serious)
            rig.Handler.EnqueueException(new HttpRequestException("reset"));
        else
            rig.Handler.Enqueue(404);

        await rig.SendAsync("GET");

        Assert.Equal(serious ? 1 : 0, rig.Logger.GetEntries(LogLevel.Warning).Count);
        if (!serious)
            Assert.Contains(rig.Logger.GetEntries(LogLevel.Debug), entry => entry.Message.Contains("failed"));
    }

    [Fact]
    public async Task FinalServerError_IsLoggedAsWarning()
    {
        using var rig = new LoopRig();
        rig.Handler.Enqueue(503);

        await rig.SendAsync("GET");

        Assert.Single(rig.Logger.GetEntries(LogLevel.Warning));
    }

    [Fact]
    public async Task SuccessfulCall_LogsNoWarningOrError()
    {
        using var rig = new LoopRig();
        rig.Handler.Enqueue(200);

        await rig.SendAsync("GET");

        Assert.Empty(rig.Logger.GetEntries(LogLevel.Warning));
        Assert.Empty(rig.Logger.GetEntries(LogLevel.Error));
    }
}
