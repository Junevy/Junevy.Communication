using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP.Exceptions;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// FTP 的失败到 <see cref="CommResult"/> 的映射，是全包唯一一处由 SDK 的异常或应答码推出
/// <see cref="CommErrorKind"/> 的地方（P6-C7、Q38）。逐行的映射表见计划第 16 节，每一行都有对应的测试。
/// 本库自己的检查（路径为空、本地源文件不存在、远程对象不存在）也从这里取结果，使分类只有一处来源。
/// </summary>
internal static class FtpErrorMap
{
    /// <summary>运行期异常 → 结果。SDK 抛出的异常不能逃出公开方法（P6-C8）。</summary>
    /// <param name="exception">SDK 或 BCL 抛出的异常；只作诊断，不参与分类之外的决策。</param>
    /// <param name="cancelledByCaller">调用方的取消令牌是否已经触发。</param>
    /// <returns>带分类的失败结果。</returns>
    public static CommResult FromException(Exception exception, bool cancelledByCaller)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (cancelledByCaller)
        {
            return Fail(CommErrorKind.Cancelled, null, "The operation was cancelled.", exception);
        }

        // 登录异常本身是应答码异常的子类，因此排在前面：第 16 节的"登录失败"行。
        if (exception is FtpAuthenticationException authentication)
        {
            return Fail(CommErrorKind.AuthenticationFailed, ParseCode(authentication.CompletionCode), ReplyText(authentication), exception);
        }

        if (exception is FtpCommandException command)
        {
            return FromReply(command);
        }

        // TLS 的认证异常可能被 SDK 包在别的异常里，因此沿内层异常链找一遍。
        if (IsTlsFailure(exception))
        {
            return Fail(CommErrorKind.AuthenticationFailed, null, $"TLS authentication failed: {exception.Message}", exception);
        }

        return FromTransport(exception);
    }

    /// <summary>本库检查出的请求错误：路径为空、上传的本地源文件不存在（不发送，P63-D5）。</summary>
    /// <param name="message">写明具体哪一项非法。</param>
    /// <returns><see cref="CommErrorKind.InvalidRequest"/> 的结果。</returns>
    public static CommResult InvalidRequest(string message) => CommResult.Fail(message, CommErrorKind.InvalidRequest);

    /// <summary>本库在服务器上检查出的"不存在"（P63-D8、D9、D10）：RemoteError，没有应答码。</summary>
    /// <param name="message">写明哪个远程对象不存在。</param>
    /// <returns><see cref="CommErrorKind.RemoteError"/> 的结果。</returns>
    public static CommResult RemoteObjectMissing(string message) => CommResult.Fail(message, CommErrorKind.RemoteError);

    /// <summary>SDK 报告失败但没有应答码（例如传输状态为失败，P63 第 16 节）：RemoteError。</summary>
    /// <param name="message">写明报告的失败内容。</param>
    /// <returns><see cref="CommErrorKind.RemoteError"/> 的结果。</returns>
    public static CommResult SdkReportedFailure(string message) => CommResult.Fail(message, CommErrorKind.RemoteError);

    /// <summary>本地文件系统错误（无法创建本地目录、写入临时文件等）：Unspecified（P63 第 16 节）。</summary>
    /// <param name="exception">本地文件操作抛出的异常。</param>
    /// <returns><see cref="CommErrorKind.Unspecified"/> 的结果。</returns>
    public static CommResult LocalFileSystem(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Fail(CommErrorKind.Unspecified, null, $"The local file operation failed: {exception.Message}", exception);
    }

    /// <summary>释放之后的操作（P6-C8）：ConnectionClosed。</summary>
    /// <returns><see cref="CommErrorKind.ConnectionClosed"/> 的结果。</returns>
    public static CommResult Disposed() => CommResult.Fail("The FTP client has been disposed.", CommErrorKind.ConnectionClosed);

    // 应答码：530 是登录失败，421、425、426 表示连接或数据连接失败，452、552 是存储空间不足，502、504 是命令未实现，其余 4xx、5xx 是远程错误。
    private static CommResult FromReply(FtpCommandException command)
    {
        long? code = ParseCode(command.CompletionCode);
        CommErrorKind kind = code switch
        {
            530 => CommErrorKind.AuthenticationFailed,
            421 or 425 or 426 => CommErrorKind.ConnectionClosed,
            452 or 552 => CommErrorKind.ResourceExhausted,
            502 or 504 => CommErrorKind.NotSupported,
            _ => CommErrorKind.RemoteError,
        };

        return Fail(kind, code, ReplyText(command), command);
    }

    // 没有应答码的运行期失败：取消（释放时进行中的操作）、超时、连接失败，以及释放之后的访问。
    private static CommResult FromTransport(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return Fail(CommErrorKind.ConnectionClosed, null, "The operation was aborted because the client was disposed or the connection was closed.", exception);
        }

        if (IsTimeout(exception))
        {
            return Fail(CommErrorKind.Timeout, null, "The FTP operation timed out.", exception);
        }

        if (exception is ObjectDisposedException)
        {
            return Fail(CommErrorKind.ConnectionClosed, null, "The FTP client has been disposed.", exception);
        }

        if (exception is SocketException or IOException)
        {
            return Fail(CommErrorKind.ConnectionClosed, null, $"The connection failed: {exception.Message}", exception);
        }

        return Fail(CommErrorKind.Unspecified, null, $"The FTP operation failed with {exception.GetType().Name}: {exception.Message}", exception);
    }

    private static bool IsTimeout(Exception exception) =>
        exception is TimeoutException
        || exception is SocketException { SocketErrorCode: SocketError.TimedOut }
        || exception is IOException { InnerException: SocketException { SocketErrorCode: SocketError.TimedOut } };

    private static bool IsTlsFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException or FtpInvalidCertificateException)
            {
                return true;
            }
        }

        return false;
    }

    private static long? ParseCode(string? code) =>
        int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static string ReplyText(FtpCommandException command) =>
        string.IsNullOrEmpty(command.CompletionCode)
            ? $"The server failed the command: {command.Message}"
            : $"The server replied {command.CompletionCode}: {command.Message}";

    private static CommResult Fail(CommErrorKind kind, long? protocolErrorCode, string message, Exception? exception) =>
        CommResult.Fail(message, kind, protocolErrorCode, exception);
}
