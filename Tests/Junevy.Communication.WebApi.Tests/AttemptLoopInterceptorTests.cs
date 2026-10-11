using System.Net.Http;
using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 尝试循环与拦截器的契约（P3-D3，计划 Task 6 测试清单"拦截器"）：执行顺序、从快照重建、Items、Fail、异常、同源检查、重放与超时。
/// </summary>
public sealed class AttemptLoopInterceptorTests
{
    [Fact]
    public async Task Interceptors_RunInRegistrationOrderThenInReverse_WithConfiguredAuthenticationFirst()
    {
        var order = new List<string>();
        var auth = new ScriptedInterceptor("auth", order);
        using var rig = new LoopRig(interceptors: new[] { new ScriptedInterceptor("a", order), new ScriptedInterceptor("b", order) },
                                    authInterceptor: auth);
        rig.Handler.Enqueue(200);

        await rig.SendAsync();

        Assert.Equal(new[] { "auth.send", "a.send", "b.send", "b.recv", "a.recv", "auth.recv" }, order);
    }

    [Fact]
    public async Task Interceptors_WithoutAConfiguredAuthentication_RunInRegistrationOrder()
    {
        var order = new List<string>();
        using var rig = new LoopRig(interceptors: new[] { new ScriptedInterceptor("a", order), new ScriptedInterceptor("b", order) });
        rig.Handler.Enqueue(200);

        await rig.SendAsync();

        Assert.Equal(new[] { "a.send", "b.send", "b.recv", "a.recv" }, order);
    }

    [Fact]
    public async Task EachAttempt_StartsFromTheSnapshot_SoModificationsDoNotAccumulate()
    {
        var headersSeen = new List<bool>();
        var bodiesSeen = new List<byte>();
        var signer = ScriptedInterceptor.Sending("signer", context =>
        {
            headersSeen.Add(context.Headers.ContainsKey("X-Sign"));
            bodiesSeen.Add(context.Body[0]);
            context.Headers["X-Sign"] = "signed";
            context.Body[0] = 99;
        });
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1, new[] { signer });
        rig.Handler.Enqueue(503).Enqueue(200);

        await rig.SendAsync("GET", body: new byte[] { 7, 8 }, contentType: "application/octet-stream");

        Assert.Equal(new[] { false, false }, headersSeen);
        Assert.Equal(new byte[] { 7, 7 }, bodiesSeen);
        Assert.All(rig.Handler.Requests, request => Assert.Equal(new byte[] { 99, 8 }, request.Body));
    }

    [Fact]
    public async Task Items_AreSharedByAllInterceptorsAndAllAttempts()
    {
        IDictionary<string, object?>? shared = null;
        var counter = ScriptedInterceptor.Sending("counter", context =>
        {
            shared = context.Items;
            context.Items["sends"] = (int)(context.Items.TryGetValue("sends", out object? value) ? value! : 0) + 1;
        });
        var reader = ScriptedInterceptor.Received("reader", context => context.Items["seenByReceived"] = context.Items["sends"]);
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1, new[] { counter, reader });
        rig.Handler.Enqueue(503).Enqueue(200);

        await rig.SendAsync("GET");

        Assert.Equal(2, shared!["sends"]);
        Assert.Equal(2, shared["seenByReceived"]);
    }

    [Fact]
    public async Task Items_AreNotSharedBetweenCalls()
    {
        var itemSets = new List<IDictionary<string, object?>>();
        var spy = ScriptedInterceptor.Sending("spy", context => itemSets.Add(context.Items));
        using var rig = new LoopRig(interceptors: new[] { spy });
        rig.Handler.Enqueue(200).Enqueue(200);

        await rig.SendAsync();
        await rig.SendAsync();

        Assert.NotSame(itemSets[0], itemSets[1]);
    }

    [Fact]
    public async Task CallIdAndAttempt_AreSeenByInterceptors()
    {
        var seen = new List<(long CallId, int Attempt)>();
        var spy = ScriptedInterceptor.Sending("spy", context => seen.Add((context.CallId, context.Attempt)));
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1, new[] { spy });
        rig.Handler.Enqueue(503).Enqueue(200).Enqueue(200);

        await rig.SendAsync("GET");
        await rig.SendAsync("GET");

        Assert.Equal(new[] { (1L, 1), (1L, 2), (2L, 1) }, seen);
    }

    [Fact]
    public async Task FailInOnSending_AbortsWithoutSendingOrRetrying()
    {
        var failing = ScriptedInterceptor.Sending("gate", context => context.Fail("blocked by policy", CommErrorKind.AuthenticationFailed));
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2, new[] { failing });

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal("blocked by policy", result.ErrorMessage);
        Assert.Null(result.StatusCode);
        Assert.Empty(rig.Handler.Requests);
        Assert.Equal(new[] { "Completed(0,False)" }, rig.Timeline);
    }

    [Fact]
    public async Task FailInOnSending_StopsTheRemainingInterceptors()
    {
        var after = new ScriptedInterceptor("after");
        var failing = ScriptedInterceptor.Sending("gate", context => context.Fail("stop", CommErrorKind.InvalidRequest));
        using var rig = new LoopRig(interceptors: new IWebApiInterceptor[] { failing, after });

        await rig.SendAsync();

        Assert.Equal(0, after.SendingCalls);
    }

    [Fact]
    public async Task ThrowingInterceptor_FailsAsUnspecifiedWithoutSendingOrRetrying()
    {
        var failure = new InvalidOperationException("token service down");
        var throwing = new ScriptedInterceptor("auth", null, (context, token) => throw failure);
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2, new[] { throwing });

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Same(failure, result.Exception);
        Assert.Contains("token service down", result.ErrorMessage);
        Assert.Empty(rig.Handler.Requests);
        Assert.Equal(new[] { "Completed(0,False)" }, rig.Timeline);
        TestLogEntry error = Assert.Single(rig.Logger.GetEntries(LogLevel.Error));
        Assert.Same(failure, error.Exception);
    }

    [Theory]
    [InlineData("http://evil.local/api/orders")]
    [InlineData("https://mes.local/api/orders")]
    [InlineData("http://mes.local:8080/api/orders")]
    public async Task ChangingTheOrigin_FailsAsInvalidRequestWithoutSending(string newUri)
    {
        var redirecting = ScriptedInterceptor.Sending("redirect", context => context.RequestUri = new Uri(newUri));
        using var rig = new LoopRig(interceptors: new[] { redirecting });

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(rig.Handler.Requests);
        Assert.Empty(rig.Sending);
    }

    [Fact]
    public async Task ChangingThePathAndQuery_IsAllowed()
    {
        var signer = ScriptedInterceptor.Sending("signer", context => context.RequestUri = new Uri(context.RequestUri.AbsoluteUri + "&sig=abc"));
        using var rig = new LoopRig(interceptors: new[] { signer });
        rig.Handler.Enqueue(200);

        WebApiResult<string> result = await rig.SendAsync("GET", "orders?id=1");

        Assert.True(result.IsSuccess);
        Assert.Equal("http://mes.local/api/orders?id=1&sig=abc", rig.Handler.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Replay_ResendsAPostAndRunsOnSendingAgain()
    {
        var attempts = new List<int>();
        var refresher = new ScriptedInterceptor("refresh", null,
                                                (context, token) =>
                                                {
                                                    attempts.Add(context.Attempt);
                                                    return Task.CompletedTask;
                                                },
                                                (context, token) =>
                                                {
                                                    if (context.StatusCode == 401)
                                                        context.RequestReplay();
                                                    return Task.CompletedTask;
                                                });
        using var rig = new LoopRig(interceptors: new[] { refresher });
        rig.Handler.Enqueue(401).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("POST", body: new byte[] { 1 }, contentType: "application/json");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Equal(new[] { 1, 2 }, attempts);
        Assert.Equal(new[] { "Sending(1)", "Response(1,401,retry=True)", "Sending(2)", "Response(2,200,retry=False)", "Completed(2,True)" }, rig.Timeline);
    }

    [Fact]
    public async Task Replay_IsHonouredOnlyOnce_TheSecondRequestIsIgnored()
    {
        var replayer = ScriptedInterceptor.Received("replay", context => context.RequestReplay());
        using var rig = new LoopRig(interceptors: new[] { replayer });
        rig.Handler.Enqueue(401).Enqueue(401).Enqueue(200, "never reached");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Equal(1, rig.Handler.PendingScriptCount);
        Assert.False(rig.Responses.Last().WillRetry);
    }

    [Fact]
    public async Task Replay_DoesNotConsumeTheRetryCount()
    {
        var replayOn401 = ScriptedInterceptor.Received("replay", context =>
        {
            if (context.StatusCode == 401)
                context.RequestReplay();
        });
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1, new[] { replayOn401 });
        rig.Handler.Enqueue(401).Enqueue(503).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.True(result.IsSuccess);
        Assert.Equal(3, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task Replay_IsNotLimitedByTheMethodRule()
    {
        var replayer = ScriptedInterceptor.Received("replay", context =>
        {
            if (context.StatusCode == 401)
                context.RequestReplay();
        });
        using var rig = new LoopRig(interceptors: new[] { replayer });
        rig.Handler.Enqueue(401).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("DELETE");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task HangingOnSending_TimesOutTheAttempt_WithoutSendingOrRaisingRequestSending()
    {
        var hanging = new ScriptedInterceptor("slow", null, async (context, token) => await Task.Delay(Timeout.Infinite, token));
        using var rig = new LoopRig(c => c.Timeout = 100, new[] { hanging });

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Empty(rig.Handler.Requests);
        Assert.Empty(rig.Sending);
    }

    [Fact]
    public async Task PerCallTimeout_OverridesTheConfiguredOne()
    {
        var hanging = new ScriptedInterceptor("slow", null, async (context, token) => await Task.Delay(Timeout.Infinite, token));
        using var rig = new LoopRig(c => c.Timeout = 600000, new[] { hanging });

        WebApiResult<string> result = await rig.SendAsync("GET", timeout: 100);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotCallOnReceived()
    {
        var spy = new ScriptedInterceptor("spy");
        using var rig = new LoopRig(interceptors: new[] { spy });
        rig.Handler.EnqueueException(new HttpRequestException("reset"));

        await rig.SendAsync("GET");

        Assert.Equal(1, spy.SendingCalls);
        Assert.Equal(0, spy.ReceivedCalls);
    }

    [Fact]
    public async Task FailInOnReceived_RewritesTheResultKeepsTheStatusAndDoesNotRetry()
    {
        var verifier = ScriptedInterceptor.Received("verify", context => context.Fail("signature mismatch", CommErrorKind.ProtocolViolation));
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2, new[] { verifier });
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Equal("signature mismatch", result.ErrorMessage);
        Assert.Equal(503, result.StatusCode);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task FailWinsOverReplayInOnReceived()
    {
        var both = ScriptedInterceptor.Received("both", context =>
        {
            context.RequestReplay();
            context.Fail("rejected", CommErrorKind.ProtocolViolation);
        });
        using var rig = new LoopRig(interceptors: new[] { both });
        rig.Handler.Enqueue(200).Enqueue(200);

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task ThrowingInOnReceived_FailsAsUnspecifiedWithoutRetrying()
    {
        var throwing = new ScriptedInterceptor("decrypt", null, null, (context, token) => throw new InvalidOperationException("bad key"));
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2, new[] { throwing });
        rig.Handler.Enqueue(503).Enqueue(200);

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Equal(503, result.StatusCode);
        Assert.Single(rig.Handler.Requests);
        Assert.Single(rig.Logger.GetEntries(LogLevel.Error));
    }

    [Fact]
    public async Task ReplacedResponseBody_IsWhatTheInterpreterAndTheEventSee()
    {
        var decrypt = ScriptedInterceptor.Received("decrypt", context => context.Body = Encoding.UTF8.GetBytes("decrypted"));
        using var rig = new LoopRig(interceptors: new[] { decrypt });
        rig.Handler.Enqueue(200, "ciphertext");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal("decrypted", result.Data);
        Assert.Equal("decrypted", Encoding.UTF8.GetString(rig.Responses.Single().Body.ToArray()));
    }

    [Fact]
    public async Task ReplacedRequestBody_IsWhatIsSentAndWhatTheEventShows()
    {
        var encrypt = ScriptedInterceptor.Sending("encrypt", context => context.Body = Encoding.UTF8.GetBytes("encrypted"));
        using var rig = new LoopRig(interceptors: new[] { encrypt });
        rig.Handler.Enqueue(200);

        await rig.SendAsync("POST", body: Encoding.UTF8.GetBytes("plain"), contentType: "text/plain");

        Assert.Equal("encrypted", Encoding.UTF8.GetString(rig.Handler.Requests.Single().Body));
        Assert.Equal("encrypted", Encoding.UTF8.GetString(rig.Sending.Single().Body.ToArray()));
    }

    [Fact]
    public void Compose_PutsTheConfiguredAuthenticationBeforeTheUserInterceptors()
    {
        var order = new List<string>();
        var auth = new ScriptedInterceptor("auth", order);
        var user = new ScriptedInterceptor("user", order);

        var chain = Http.InterceptorChain.Create(auth, new IWebApiInterceptor[] { user }, new Junevy.Communication.Testing.TestLogger());

        Assert.Equal(new IWebApiInterceptor[] { auth, user }, chain.Interceptors);
    }
}
