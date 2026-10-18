using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP.Exceptions;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 第 16 节错误映射表的逐行测试（P6-C7、Q38）：每一行都构造 SDK 的异常或应答码，断言分类与协议码。
/// </summary>
public sealed class FtpErrorMapTests
{
    public static TheoryData<string, CommErrorKind> ReplyRows => new()
    {
        { "421", CommErrorKind.ConnectionClosed },
        { "425", CommErrorKind.ConnectionClosed },
        { "426", CommErrorKind.ConnectionClosed },
        { "452", CommErrorKind.ResourceExhausted },
        { "552", CommErrorKind.ResourceExhausted },
        { "502", CommErrorKind.NotSupported },
        { "504", CommErrorKind.NotSupported },
        { "450", CommErrorKind.RemoteError },
        { "500", CommErrorKind.RemoteError },
        { "550", CommErrorKind.RemoteError },
        { "553", CommErrorKind.RemoteError },
    };

    [Theory]
    [MemberData(nameof(ReplyRows))]
    public void ReplyCode_MapsToItsKind_AndKeepsTheCode(string code, CommErrorKind kind)
    {
        var exception = new FtpCommandException(code, "The server replied.");

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, kind, long.Parse(code));
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public void Login530_IsAuthenticationFailed_WithTheCode()
    {
        var exception = new FtpAuthenticationException("530", "Login incorrect.");

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.AuthenticationFailed, 530);
    }

    [Fact]
    public void Reply530_OutsideTheLoginException_IsAuthenticationFailed()
    {
        var exception = new FtpCommandException("530", "Not logged in.");

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.AuthenticationFailed, 530);
    }

    [Fact]
    public void UntrustedCertificate_IsAuthenticationFailed_WithoutCode()
    {
        var exception = new FtpInvalidCertificateException("The certificate is not trusted.");

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.AuthenticationFailed, null);
    }

    [Fact]
    public void TlsAuthenticationException_IsAuthenticationFailed_WithoutCode()
    {
        var exception = new AuthenticationException("The remote certificate is invalid.");

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.AuthenticationFailed, null);
    }

    [Fact]
    public void TlsAuthenticationException_InsideAnotherException_IsAuthenticationFailed()
    {
        var exception = new IOException("The TLS handshake failed.", new AuthenticationException("invalid"));

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.AuthenticationFailed, null);
    }

    [Fact]
    public void CallerCancelled_IsCancelled()
    {
        CommResult result = FtpErrorMap.FromException(new OperationCanceledException(), cancelledByCaller: true);

        AssertFailure(result, CommErrorKind.Cancelled, null);
    }

    [Fact]
    public void OperationCanceled_NotCancelledByCaller_IsConnectionClosed()
    {
        // 释放客户端时正在进行的操作以这种方式结束（P63-D4，计划 Task 6）。
        CommResult result = FtpErrorMap.FromException(new OperationCanceledException(), cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.ConnectionClosed, null);
    }

    [Theory]
    [InlineData(TimeoutKind.TimeoutException)]
    [InlineData(TimeoutKind.SocketTimedOut)]
    [InlineData(TimeoutKind.IOExceptionWithSocketTimedOut)]
    public void Timeout_IsTimeout(TimeoutKind kind)
    {
        Exception exception = kind switch
        {
            TimeoutKind.TimeoutException => new TimeoutException("The operation timed out."),
            TimeoutKind.SocketTimedOut => new SocketException((int)SocketError.TimedOut),
            _ => new IOException("The read failed.", new SocketException((int)SocketError.TimedOut)),
        };

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.Timeout, null);
    }

    public enum TimeoutKind
    {
        TimeoutException,
        SocketTimedOut,
        IOExceptionWithSocketTimedOut,
    }

    [Fact]
    public void ConnectionRefused_IsConnectionClosed_WithoutCode()
    {
        var exception = new SocketException((int)SocketError.ConnectionRefused);

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.ConnectionClosed, null);
    }

    [Fact]
    public void ControlConnectionLost_IsConnectionClosed_WithoutCode()
    {
        var exception = new IOException("Unable to read data from the transport connection.");

        CommResult result = FtpErrorMap.FromException(exception, cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.ConnectionClosed, null);
    }

    [Fact]
    public void RemoteFailureWithoutAReplyCode_IsRemoteError()
    {
        CommResult result = FtpErrorMap.RemoteFailure("The transfer was reported as failed.");

        AssertFailure(result, CommErrorKind.RemoteError, null);
    }

    [Fact]
    public void MissingRemoteObject_CheckedByTheLibrary_IsRemoteError()
    {
        CommResult result = FtpErrorMap.RemoteObjectMissing("The remote directory does not exist.");

        AssertFailure(result, CommErrorKind.RemoteError, null);
    }

    [Fact]
    public void EmptyPath_IsInvalidRequest()
    {
        CommResult result = FtpErrorMap.InvalidRequest("The remote path must not be empty.");

        AssertFailure(result, CommErrorKind.InvalidRequest, null);
    }

    [Fact]
    public void LocalFileSystemError_IsUnspecified()
    {
        var exception = new UnauthorizedAccessException("Access to the path is denied.");

        CommResult result = FtpErrorMap.LocalFileSystem(exception);

        AssertFailure(result, CommErrorKind.Unspecified, null);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public void OperationAfterDisposal_IsConnectionClosed()
    {
        CommResult result = FtpErrorMap.Disposed();

        AssertFailure(result, CommErrorKind.ConnectionClosed, null);
    }

    [Fact]
    public void OtherSdkException_IsUnspecified()
    {
        CommResult result = FtpErrorMap.FromException(new InvalidOperationException("Unexpected."), cancelledByCaller: false);

        AssertFailure(result, CommErrorKind.Unspecified, null);
    }

    private static void AssertFailure(CommResult result, CommErrorKind kind, long? protocolErrorCode)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(kind, result.ErrorKind);
        Assert.Equal(protocolErrorCode, result.ProtocolErrorCode);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }
}
