using System.Net.Http;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 尝试循环的重试行为（P3-D10，计划 Task 6 测试清单"重试规则表"）：用 <see cref="StubHttpHandler"/> 驱动，不开套接字，退避为 0。
/// 需要观察退避时长的用例在 <c>ResponseReceived</c> 里取消调用：事件参数已带 <c>RetryDelay</c>，测试不必真的等待。
/// </summary>
public sealed class AttemptLoopRetryTests
{
    [Fact]
    public async Task Get503_IsRetriedAndSucceeds()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2);
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Data);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task Post503_WithoutKey_IsNotRetried()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2);
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("POST", body: new byte[] { 1 }, contentType: "application/json");

        Assert.False(result.IsSuccess);
        Assert.Equal(503, result.StatusCode);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task PostWithIdempotencyKey_IsRetriedAndEveryAttemptCarriesTheHeader()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("POST", idempotencyKey: "order-42", body: new byte[] { 1 }, contentType: "application/json");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.All(rig.Handler.Requests, request => Assert.Equal("order-42", request.Headers["Idempotency-Key"]));
    }

    [Fact]
    public async Task Post503_WithRetryUnsafeMethods_IsRetried()
    {
        using var rig = new LoopRig(c =>
        {
            c.Retry.RetryCount = 1;
            c.Retry.RetryUnsafeMethods = true;
        });
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("POST", body: new byte[] { 1 }, contentType: "application/json");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OtherNonIdempotentMethods_AreNotRetriedByDefault(string method)
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        await rig.SendAsync(method);

        Assert.Single(rig.Handler.Requests);
    }

    [Theory]
    [InlineData(400, 1)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(404, 1)]
    [InlineData(409, 1)]
    [InlineData(501, 1)]
    [InlineData(408, 2)]
    [InlineData(429, 2)]
    [InlineData(500, 2)]
    [InlineData(502, 2)]
    [InlineData(503, 2)]
    [InlineData(504, 2)]
    public async Task Status_DecidesWhetherAGetIsRetried(int status, int expectedRequests)
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        rig.Handler.Enqueue(status).Enqueue(200, "ok");

        await rig.SendAsync("GET");

        Assert.Equal(expectedRequests, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task ServerErrorWithARemoteCode_IsNeverRetried()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2);
        rig.Handler.Enqueue(500, "denied", headers: new Dictionary<string, string> { ["X-Remote-Code"] = "E42" }).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal("E42", result.RemoteCode);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task NetworkFailure_IsRetried()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        rig.Handler.EnqueueException(new HttpRequestException("connection reset")).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task AttemptTimeout_IsRetried()
    {
        using var rig = new LoopRig(c =>
        {
            c.Retry.RetryCount = 1;
            c.Timeout = 100;
        });
        rig.Handler.EnqueueHang().Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task OversizedResponse_IsNotRetried()
    {
        using var rig = new LoopRig(c =>
        {
            c.Retry.RetryCount = 1;
            c.MaxResponseBodySize = 4;
        });
        rig.Handler.Enqueue(200, "too large").Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task RetryCountUsedUp_ReturnsTheLastRealResult()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2);
        rig.Handler.Enqueue(503).Enqueue(502).Enqueue(504).Enqueue(200, "never reached");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.False(result.IsSuccess);
        Assert.Equal(504, result.StatusCode);
        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal(1, rig.Handler.PendingScriptCount);
        Assert.False(rig.Responses.Last().WillRetry);
    }

    [Fact]
    public async Task RetryAfterSeconds_RaisesTheDelayAboveTheBackoff()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        rig.Handler.Enqueue(503, headers: new Dictionary<string, string> { ["Retry-After"] = "2" });

        WebApiResult<string> result = await CancelAtTheFirstRetryDecisionAsync(rig);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.True(rig.Responses.First().WillRetry);
        Assert.Equal(2000, rig.Responses.First().RetryDelay);
    }

    [Fact]
    public async Task RetryAfterHttpDate_IsAdopted()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1);
        string date = DateTimeOffset.UtcNow.AddSeconds(10).ToString("R");
        rig.Handler.Enqueue(429, headers: new Dictionary<string, string> { ["Retry-After"] = date });

        await CancelAtTheFirstRetryDecisionAsync(rig);

        Assert.True(rig.Responses.First().WillRetry);
        Assert.InRange(rig.Responses.First().RetryDelay, 5000, 10000);
    }

    [Fact]
    public async Task RetryAfterBeyondTheLimit_IsNotRetried()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 2);
        rig.Handler.Enqueue(503, headers: new Dictionary<string, string> { ["Retry-After"] = "31" }).Enqueue(200, "ok");

        WebApiResult<string> result = await rig.SendAsync("GET");

        Assert.Equal(503, result.StatusCode);
        Assert.Single(rig.Handler.Requests);
        Assert.False(rig.Responses.Single().WillRetry);
    }

    [Fact]
    public async Task CustomBackoff_ProvidesTheDelay()
    {
        using var rig = new LoopRig(c => c.Retry.RetryCount = 1, backoff: new FixedIntervalBackoff(250));
        rig.Handler.Enqueue(503);

        await CancelAtTheFirstRetryDecisionAsync(rig);

        Assert.Equal(250, rig.Responses.First().RetryDelay);
    }

    [Fact]
    public async Task CancelledWhileWaitingToRetry_ReturnsCancelledAndSendsNothingMore()
    {
        using var rig = new LoopRig(c =>
        {
            c.Retry.RetryCount = 3;
            c.Retry.RetryDelay = 60000;
        });
        rig.Handler.Enqueue(503).Enqueue(200, "ok");

        WebApiResult<string> result = await CancelAtTheFirstRetryDecisionAsync(rig);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Single(rig.Handler.Requests);
        WebApiCompletedEventArgs completed = Assert.Single(rig.Completed);
        Assert.False(completed.IsSuccess);
        Assert.Equal(CommErrorKind.Cancelled, completed.ErrorKind);
        Assert.Equal(1, completed.Attempts);
    }

    private static async Task<WebApiResult<string>> CancelAtTheFirstRetryDecisionAsync(LoopRig rig)
    {
        using var source = new CancellationTokenSource();
        rig.Events.ResponseReceived += (sender, args) => source.Cancel();
        return await rig.SendAsync("GET", cancellationToken: source.Token);
    }
}
