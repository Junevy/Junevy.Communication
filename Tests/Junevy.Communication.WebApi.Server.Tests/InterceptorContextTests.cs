using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>拦截器上下文的规则（P32-D10）：<c>Reject</c> 只接受 4xx 与 5xx，不能重复拒绝，业务码与消息不能为 null。</summary>
public sealed class InterceptorContextTests
{
    [Theory]
    [InlineData(399)]
    [InlineData(600)]
    public void Reject_OutsideTheErrorRange_IsRejected(int statusCode)
    {
        var context = new WebApiServerRequestingContext(RequestFactory.Create());

        Assert.Throws<ArgumentOutOfRangeException>(() => context.Reject(statusCode, "E", "message"));
    }

    [Fact]
    public void Reject_RecordsTheDetailsOnce()
    {
        var context = new WebApiServerRequestingContext(RequestFactory.Create());

        context.Reject(429, "E429", "slow down");

        Assert.NotNull(context.RejectionDetails);
        Assert.Equal(429, context.RejectionDetails!.StatusCode);
        Assert.Equal("E429", context.RejectionDetails.Code);
        Assert.Throws<InvalidOperationException>(() => context.Reject(403, "E403", "again"));
    }

    [Fact]
    public void Reject_RejectsNullCodeOrMessage()
    {
        var context = new WebApiServerRequestingContext(RequestFactory.Create());

        Assert.Throws<ArgumentNullException>(() => context.Reject(400, null!, "message"));
        Assert.Throws<ArgumentNullException>(() => context.Reject(400, "E", null!));
    }

    [Fact]
    public void RespondingContext_ExposesTheStatusAndTheHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "1" };
        var context = new WebApiServerRespondingContext(RequestFactory.Create(), 503, WebApiServerOutcome.Rejected, headers);

        Assert.Equal(503, context.StatusCode);
        Assert.Equal("1", context.Headers["retry-after"]);
    }
}
