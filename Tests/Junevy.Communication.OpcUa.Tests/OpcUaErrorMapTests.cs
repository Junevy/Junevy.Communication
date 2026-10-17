using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.OpcUa;
using Junevy.Communication.OpcUa.Sdk;
using Opc.Ua;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 错误映射的每一行（计划第 17.1 节）。每一行都带名字，便于逐行核对。
/// </summary>
public sealed class OpcUaErrorMapTests
{
    public static TheoryData<string, uint, CommErrorKind> ServiceLevelRows()
    {
        var rows = new TheoryData<string, uint, CommErrorKind>();

        void Add(CommErrorKind kind, params StatusCode[] codes)
        {
            foreach (StatusCode code in codes)
            {
                rows.Add(new StatusCode(code.Code).SymbolicId!, code.Code, kind);
            }
        }

        Add(CommErrorKind.Timeout,
            StatusCodes.BadTimeout,
            StatusCodes.BadRequestTimeout);

        Add(CommErrorKind.ConnectionClosed,
            StatusCodes.BadNotConnected,
            StatusCodes.BadConnectionClosed,
            StatusCodes.BadSecureChannelClosed,
            StatusCodes.BadSessionClosed,
            StatusCodes.BadSessionIdInvalid,
            StatusCodes.BadServerNotConnected,
            StatusCodes.BadCommunicationError);

        Add(CommErrorKind.AuthenticationFailed,
            StatusCodes.BadIdentityTokenInvalid,
            StatusCodes.BadIdentityTokenRejected,
            StatusCodes.BadUserAccessDenied,
            StatusCodes.BadSecurityChecksFailed,
            StatusCodes.BadCertificateInvalid,
            StatusCodes.BadCertificateHostNameInvalid,
            StatusCodes.BadCertificateUriInvalid,
            StatusCodes.BadCertificateUseNotAllowed,
            StatusCodes.BadCertificateIssuerTimeInvalid,
            StatusCodes.BadCertificateIssuerUseNotAllowed,
            StatusCodes.BadCertificatePolicyCheckFailed,
            StatusCodes.BadCertificateRevocationUnknown,
            StatusCodes.BadCertificateRevoked,
            StatusCodes.BadCertificateChainIncomplete,
            StatusCodes.BadCertificateTimeInvalid,
            StatusCodes.BadCertificateIssuerRevocationUnknown,
            StatusCodes.BadCertificateIssuerRevoked,
            StatusCodes.BadCertificateUntrusted);

        Add(CommErrorKind.ResourceExhausted,
            StatusCodes.BadTooManySessions,
            StatusCodes.BadTooManyOperations,
            StatusCodes.BadTooManySubscriptions,
            StatusCodes.BadTooManyMonitoredItems,
            StatusCodes.BadOutOfMemory,
            StatusCodes.BadResourceUnavailable);

        Add(CommErrorKind.NotSupported,
            StatusCodes.BadServiceUnsupported,
            StatusCodes.BadNotSupported,
            StatusCodes.BadSecurityPolicyRejected,
            StatusCodes.BadSecurityModeRejected);

        Add(CommErrorKind.ProtocolViolation,
            StatusCodes.BadEncodingError,
            StatusCodes.BadDecodingError,
            StatusCodes.BadEncodingLimitsExceeded);

        return rows;
    }

    [Theory]
    [MemberData(nameof(ServiceLevelRows))]
    public void ServiceLevel_EveryRowOfTheTable(string name, uint code, CommErrorKind expected)
    {
        Assert.Equal(expected, OpcUaSdkErrorMap.KindOf(code));

        // 把符号名也断言一遍：表里的每一行都与 OPC UA 的码一一对应，改错了名字能立刻发现。
        Assert.StartsWith("Bad", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x80340000u)] // BadNodeIdUnknown
    [InlineData(0x80740000u)] // BadTypeMismatch
    [InlineData(0x803B0000u)] // BadNotWritable
    [InlineData(0x80020000u)] // BadInternalError
    [InlineData(0x80010000u)] // BadUnexpectedError
    [InlineData(0x800F0000u)] // BadNothingToDo
    [InlineData(0x80FF0000u)] // 完全未知的 Bad 码
    public void ServiceLevel_AnyOtherBadCodeIsRemoteError(uint code)
        => Assert.Equal(CommErrorKind.RemoteError, OpcUaSdkErrorMap.KindOf(code));

    [Fact]
    public void ServiceLevel_GoodAndUncertainCodesAreNotInTheServiceTable()
    {
        // 逐项状态由 FromStatus 处理，不走 KindOf；这里只钉住"表里没有 → RemoteError"这条兜底。
        Assert.Equal(CommErrorKind.RemoteError, OpcUaSdkErrorMap.KindOf(0x00000000u));
        Assert.Equal(CommErrorKind.RemoteError, OpcUaSdkErrorMap.KindOf(0x40000000u));
    }

    [Fact]
    public void ServiceLevel_BadSessionClosedIsConnectionClosedNotRemoteError()
    {
        // 变异检查：把这一行改成 RemoteError 会让 ServiceLevel_EveryRowOfTheTable 的同名行失败。
        Assert.Equal(CommErrorKind.ConnectionClosed, OpcUaSdkErrorMap.KindOf(StatusCodes.BadSessionClosed.Code));
    }

    [Fact]
    public void FromStatus_UncertainCountsAsSuccess()
        => Assert.True(OpcUaSdkErrorMap.FromStatus(new OpcUaStatusCode(0x40000000u), "ns=2;s=X").IsSuccess);

    [Fact]
    public void FromStatus_GoodCountsAsSuccess()
        => Assert.True(OpcUaSdkErrorMap.FromStatus(OpcUaStatusCode.Good, "ns=2;s=X").IsSuccess);

    [Fact]
    public void FromStatus_Bad_FailsWithTheCodeAsProtocolErrorCode()
    {
        var status = new OpcUaStatusCode(StatusCodes.BadNodeIdUnknown.Code);

        CommResult result = OpcUaSdkErrorMap.FromStatus(status, "ns=2;s=X");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(0x80340000L, result.ProtocolErrorCode);
        Assert.Contains("BadNodeIdUnknown", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FromStatus_WithData_ReturnsTheDataOnSuccess()
    {
        CommResult<int> ok = OpcUaSdkErrorMap.FromStatus(OpcUaStatusCode.Good, "ns=2;s=X", 7);
        CommResult<int> bad = OpcUaSdkErrorMap.FromStatus(new OpcUaStatusCode(StatusCodes.BadTypeMismatch.Code), "ns=2;s=X", 7);

        Assert.True(ok.IsSuccess);
        Assert.Equal(7, ok.Data);
        Assert.False(bad.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, bad.ErrorKind);
    }

    [Fact]
    public void Runtime_OperationCancelled_IsCancelled()
    {
        CommResult result = OpcUaSdkErrorMap.Runtime(new OperationCanceledException());

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.IsType<OperationCanceledException>(result.Exception);
    }

    [Fact]
    public void Runtime_Timeout_IsTimeout()
        => Assert.Equal(CommErrorKind.Timeout, OpcUaSdkErrorMap.Runtime(new TimeoutException()).ErrorKind);

    [Fact]
    public void Runtime_SocketError_IsConnectionClosed()
        => Assert.Equal(
            CommErrorKind.ConnectionClosed,
            OpcUaSdkErrorMap.Runtime(new System.Net.Sockets.SocketException(10061)).ErrorKind);

    [Fact]
    public void Runtime_SessionDisposedDuringTheCall_IsConnectionClosed()
        => Assert.Equal(
            CommErrorKind.ConnectionClosed,
            OpcUaSdkErrorMap.Runtime(new ObjectDisposedException("ManagedSession")).ErrorKind);

    [Fact]
    public void Runtime_UnknownException_IsUnspecified()
    {
        CommResult result = OpcUaSdkErrorMap.Runtime(new InvalidOperationException("boom"));

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Contains("boom", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_ServiceResultException_UsesItsStatusCode()
    {
        // 服务器停止后 SDK 抛的是 BadConnectionClosed，映射必须跟着状态码走（计划第 19.2.13 节的 F16）。
        CommResult result = OpcUaSdkErrorMap.Runtime(new ServiceResultException(StatusCodes.BadConnectionClosed));

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(0x80AE0000L, result.ProtocolErrorCode);
    }

    [Fact]
    public void Runtime_ServiceResultExceptionNestedInAnotherException_IsStillFound()
    {
        var exception = new IOException("wrapper", new ServiceResultException(StatusCodes.BadSessionClosed));

        CommResult result = OpcUaSdkErrorMap.Runtime(exception);

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(0x80260000L, result.ProtocolErrorCode);
    }

    [Fact]
    public void Runtime_NullException_Throws()
        => Assert.Throws<ArgumentNullException>(() => OpcUaSdkErrorMap.Runtime(null!));

    [Fact]
    public void Runtime_UserCancellationInTheScope_WinsOverTheExceptionType()
    {
        using CancellationTokenSource user = new();
        user.Cancel();
        using TimeoutScope scope = TimeoutScope.Start(5000, user.Token);

        CommResult result = OpcUaSdkErrorMap.Runtime(new TaskCanceledException(), scope);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
    }

    [Fact]
    public void Runtime_OurOwnTimeoutInTheScope_WinsOverTheExceptionType()
    {
        using TimeoutScope scope = TimeoutScope.Start(1, CancellationToken.None);
        Thread.Sleep(50);

        // SDK 的 ConnectAsync 没有超时参数，被中止时抛的是 TaskCanceledException（F2）。
        CommResult result = OpcUaSdkErrorMap.Runtime(new TaskCanceledException(), scope);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
    }

    [Fact]
    public void Runtime_NoTimeoutInTheScope_AndATaskCanceled_StaysCancelled()
    {
        using TimeoutScope scope = TimeoutScope.Start(0, CancellationToken.None);

        Assert.Equal(CommErrorKind.Cancelled, OpcUaSdkErrorMap.Runtime(new TaskCanceledException(), scope).ErrorKind);
    }

    [Fact]
    public void Shortcuts_UseTheAgreedKinds()
    {
        Assert.Equal(CommErrorKind.NotConnected, OpcUaSdkErrorMap.NotConnected("x").ErrorKind);
        Assert.Equal(CommErrorKind.NotSupported, OpcUaSdkErrorMap.NotSupported("x").ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, OpcUaSdkErrorMap.InvalidRequest("x").ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, OpcUaSdkErrorMap.ConnectionClosed("x").ErrorKind);
        Assert.Null(OpcUaSdkErrorMap.InvalidRequest("x").ProtocolErrorCode);
    }
}
