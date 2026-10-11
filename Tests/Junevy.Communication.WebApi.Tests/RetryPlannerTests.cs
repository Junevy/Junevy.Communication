using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="RetryPlanner"/> 的规则表测试（P3-D10）：可重试的结果、方法规则、次数、退避与 <c>Retry-After</c>。
/// 尝试循环里的端到端行为见 <see cref="AttemptLoopRetryTests"/>。
/// </summary>
public sealed class RetryPlannerTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("120", 120000)]
    [InlineData("0", 0)]
    [InlineData("  7 ", 7000)]
    [InlineData("Sun, 11 Oct 2026 08:00:10 GMT", 10000)]
    [InlineData("Sun, 11 Oct 2026 07:00:00 GMT", 0)]
    [InlineData("-5", null)]
    [InlineData("soon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseRetryAfter_AcceptsSecondsAndHttpDates(string? value, int? expected)
    {
        Assert.Equal(expected, RetryPlanner.ParseRetryAfter(value, Now));
    }

    [Theory]
    [InlineData("GET", false, false, true)]
    [InlineData("HEAD", false, false, true)]
    [InlineData("OPTIONS", false, false, true)]
    [InlineData("get", false, false, true)]
    [InlineData("POST", false, false, false)]
    [InlineData("PUT", false, false, false)]
    [InlineData("PATCH", false, false, false)]
    [InlineData("DELETE", false, false, false)]
    [InlineData("POST", true, false, true)]
    [InlineData("DELETE", true, false, true)]
    [InlineData("POST", false, true, true)]
    [InlineData("PUT", false, true, true)]
    public void NextDelay_MethodRule(string method, bool hasIdempotencyKey, bool retryUnsafeMethods, bool expectedRetry)
    {
        RetryPlanner planner = Planner(retryCount: 1, retryUnsafeMethods: retryUnsafeMethods);

        int? delay = planner.NextDelay(0, method, hasIdempotencyKey, CommErrorKind.ConnectionClosed, null, null, null, Now);

        Assert.Equal(expectedRetry, delay.HasValue);
    }

    [Theory]
    [InlineData(CommErrorKind.ConnectionClosed, null, null, true)]
    [InlineData(CommErrorKind.Timeout, null, null, true)]
    [InlineData(CommErrorKind.RemoteError, 408, null, true)]
    [InlineData(CommErrorKind.ResourceExhausted, 429, null, true)]
    [InlineData(CommErrorKind.RemoteError, 500, null, true)]
    [InlineData(CommErrorKind.RemoteError, 502, null, true)]
    [InlineData(CommErrorKind.RemoteError, 503, null, true)]
    [InlineData(CommErrorKind.RemoteError, 504, null, true)]
    [InlineData(CommErrorKind.RemoteError, 400, null, false)]
    [InlineData(CommErrorKind.AuthenticationFailed, 401, null, false)]
    [InlineData(CommErrorKind.RemoteError, 404, null, false)]
    [InlineData(CommErrorKind.RemoteError, 501, null, false)]
    [InlineData(CommErrorKind.RemoteError, 500, "E42", false)]
    [InlineData(CommErrorKind.RemoteError, 200, "E42", false)]
    [InlineData(CommErrorKind.ProtocolViolation, 200, null, false)]
    [InlineData(CommErrorKind.InvalidRequest, null, null, false)]
    [InlineData(CommErrorKind.Cancelled, null, null, false)]
    [InlineData(CommErrorKind.Unspecified, null, null, false)]
    [InlineData(CommErrorKind.None, 200, null, false)]
    public void NextDelay_ResultRule(CommErrorKind kind, int? statusCode, string? remoteCode, bool expectedRetry)
    {
        RetryPlanner planner = Planner(retryCount: 1);

        int? delay = planner.NextDelay(0, "GET", false, kind, statusCode, remoteCode, null, Now);

        Assert.Equal(expectedRetry, delay.HasValue);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(2, 2, false)]
    public void NextDelay_StopsWhenTheRetryCountIsUsedUp(int retryCount, int retriesUsed, bool expectedRetry)
    {
        RetryPlanner planner = Planner(retryCount);

        int? delay = planner.NextDelay(retriesUsed, "GET", false, CommErrorKind.ConnectionClosed, null, null, null, Now);

        Assert.Equal(expectedRetry, delay.HasValue);
    }

    [Fact]
    public void NextDelay_UsesTheConfiguredFixedDelay()
    {
        RetryPlanner planner = Planner(retryCount: 3, retryDelay: 250);

        Assert.Equal(250, planner.NextDelay(0, "GET", false, CommErrorKind.Timeout, null, null, null, Now));
        Assert.Equal(250, planner.NextDelay(2, "GET", false, CommErrorKind.Timeout, null, null, null, Now));
    }

    [Fact]
    public void NextDelay_UsesTheSuppliedBackoffWithAOneBasedRetryNumber()
    {
        var backoff = new ExponentialBackoff(100, 10000, 2.0, 0.0);
        RetryPlanner planner = Planner(retryCount: 3, backoff: backoff);

        Assert.Equal(backoff.GetDelay(1), planner.NextDelay(0, "GET", false, CommErrorKind.Timeout, null, null, null, Now));
        Assert.Equal(backoff.GetDelay(3), planner.NextDelay(2, "GET", false, CommErrorKind.Timeout, null, null, null, Now));
    }

    [Fact]
    public void NextDelay_BackoffGivingUp_MeansNoRetry()
    {
        RetryPlanner planner = Planner(retryCount: 3, backoff: new FixedIntervalBackoff(10, 1));

        Assert.NotNull(planner.NextDelay(0, "GET", false, CommErrorKind.Timeout, null, null, null, Now));
        Assert.Null(planner.NextDelay(1, "GET", false, CommErrorKind.Timeout, null, null, null, Now));
    }

    [Theory]
    [InlineData(503, "2", 100, 2000)]
    [InlineData(429, "2", 100, 2000)]
    [InlineData(503, "2", 5000, 5000)]
    [InlineData(503, "30", 100, 30000)]
    [InlineData(503, "0", 100, 100)]
    [InlineData(503, "garbage", 100, 100)]
    [InlineData(500, "2", 100, 100)]
    public void NextDelay_RetryAfter_RaisesTheDelayOnly_For429And503(int statusCode, string retryAfter, int backoffDelay, int expected)
    {
        RetryPlanner planner = Planner(retryCount: 1, retryDelay: backoffDelay);
        CommErrorKind kind = statusCode == 429 ? CommErrorKind.ResourceExhausted : CommErrorKind.RemoteError;

        Assert.Equal(expected, planner.NextDelay(0, "GET", false, kind, statusCode, null, retryAfter, Now));
    }

    [Theory]
    [InlineData("31")]
    [InlineData("3600")]
    [InlineData("Sun, 11 Oct 2026 08:01:00 GMT")]
    public void NextDelay_RetryAfterBeyondThirtySeconds_MeansNoRetry(string retryAfter)
    {
        RetryPlanner planner = Planner(retryCount: 1);

        Assert.Null(planner.NextDelay(0, "GET", false, CommErrorKind.RemoteError, 503, null, retryAfter, Now));
    }

    private static RetryPlanner Planner(int retryCount, int retryDelay = 0, bool retryUnsafeMethods = false, IBackoffPolicy? backoff = null)
    {
        var config = new TestEndpointConfig
        {
            Retry = new WebApiRetryOptions { RetryCount = retryCount, RetryDelay = retryDelay, RetryUnsafeMethods = retryUnsafeMethods },
        };
        return new RetryPlanner(EndpointConfigRules.CreateSettings(config), backoff);
    }
}
