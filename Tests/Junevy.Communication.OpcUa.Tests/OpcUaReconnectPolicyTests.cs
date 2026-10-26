using Junevy.Communication.OpcUa.Sdk;
using Opc.Ua;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 重连策略的那一层（F34）：首次连接被身份或证书类原因拒绝时停止重试，其余情况与会话建立之后照常退避。
/// 端到端的效果（错的密码很快得到 AuthenticationFailed）见 <see cref="OpcUaUserNameLoginTests"/>。
/// </summary>
public sealed class OpcUaReconnectPolicyTests
{
    public static TheoryData<string, uint> AuthenticationRejections() => Rows(
        StatusCodes.BadUserAccessDenied,
        StatusCodes.BadIdentityTokenRejected,
        StatusCodes.BadIdentityTokenInvalid,
        StatusCodes.BadCertificateUntrusted);

    public static TheoryData<string, uint> OtherStatuses() => Rows(
        StatusCodes.BadNotConnected,
        StatusCodes.BadTimeout,
        StatusCodes.Good);

    [Theory]
    [MemberData(nameof(AuthenticationRejections))]
    public void BeforeTheSessionIsEstablished_AnAuthenticationRejection_StopsRetrying(string name, uint status)
    {
        OpcUaSdkReconnectPolicy policy = Create();

        bool decided = policy.TryGetNextDelay(1, new StatusCode(status), null, out TimeSpan? delay, CancellationToken.None);

        Assert.True(decided, name);
        Assert.Null(delay);
    }

    [Theory]
    [MemberData(nameof(OtherStatuses))]
    public void BeforeTheSessionIsEstablished_OtherStatuses_KeepBackingOff(string name, uint status)
    {
        OpcUaSdkReconnectPolicy policy = Create();

        TimeSpan? delay = NextDelay(policy, new StatusCode(status));

        Assert.True(delay.HasValue, name);
        Assert.InRange(delay!.Value.TotalMilliseconds, 1, 1000 * 1.2);
    }

    [Fact]
    public void AfterTheSessionIsEstablished_AnAuthenticationRejection_KeepsBackingOff()
    {
        OpcUaSdkReconnectPolicy policy = Create();
        policy.MarkEstablished();

        Assert.NotNull(NextDelay(policy, StatusCodes.BadUserAccessDenied));
    }

    private static OpcUaSdkReconnectPolicy Create() => new(new OpcUaClientOptions
    {
        EndpointUrl = "opc.tcp://127.0.0.1:4840/",
        ReconnectInterval = 200,
        MaxReconnectInterval = 1000,
    });

    // 名字只用来让每一行在测试结果里可读。
    private static TheoryData<string, uint> Rows(params StatusCode[] codes)
    {
        var rows = new TheoryData<string, uint>();
        foreach (StatusCode code in codes)
        {
            rows.Add(new StatusCode(code.Code).SymbolicId ?? "Good", code.Code);
        }

        return rows;
    }

    // 内层策略对非"服务器忙"状态可能不给自适应结果，这时 SDK 退回 GetNextDelay，这里照做。
    private static TimeSpan? NextDelay(OpcUaSdkReconnectPolicy policy, StatusCode status)
        => policy.TryGetNextDelay(1, status, null, out TimeSpan? delay, CancellationToken.None)
            ? delay
            : policy.GetNextDelay(1, CancellationToken.None);
}
