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
    /// <summary>
    /// 运行期异常 → 结果。SDK 抛出的异常不能逃出公开方法（P6-C8）。调用方取消不经过这里：生命周期先判断调用方的令牌，
    /// 再调用 <see cref="Cancelled"/>，因此这里的 <see cref="OperationCanceledException"/> 只来自释放或连接关闭。
    /// </summary>
    /// <param name="exception">SDK 或 BCL 抛出的异常；只作诊断。</param>
    /// <returns>带分类的失败结果。</returns>
    public static CommResult FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // SDK 常把应答码异常包在别的异常里（例如上传失败时是 FtpException，缺少对象时是 FtpMissingObjectException），
        // 因此沿内层异常链找第一个应答码异常。登录异常是它的子类，排在前面。
        FtpCommandException? command = FindCommandException(exception);
        if (command is FtpAuthenticationException authentication)
        {
            return Fail(CommErrorKind.AuthenticationFailed, ParseCode(authentication.CompletionCode), ReplyText(authentication), exception);
        }

        if (command is not null)
        {
            return FromReplyCode(command.CompletionCode, ReplyText(command), exception);
        }

        // TLS 的认证异常可能被 SDK 包在别的异常里，因此沿内层异常链找一遍。
        if (IsTlsFailure(exception))
        {
            return Fail(CommErrorKind.AuthenticationFailed, null, $"TLS authentication failed: {exception.Message}", exception);
        }

        return FromTransport(exception);
    }

    /// <summary>
    /// SDK 报告失败但没有抛异常（传输返回 Failed 或 false，例如下载中断，第 18.2 节 F24；创建目录返回 false，F15）：
    /// 最后一条应答是 4xx 或 5xx 时按应答码分类（426 → ConnectionClosed，550 → RemoteError），否则为 RemoteError。
    /// </summary>
    /// <param name="lastReplyCode">SDK 最后一条应答的代码，可能为 null。</param>
    /// <param name="message">失败的描述。</param>
    /// <returns>带分类的失败结果。</returns>
    public static CommResult LastReplyFailure(string? lastReplyCode, string message) =>
        int.TryParse(lastReplyCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) && code >= 400
            ? FromReplyCode(lastReplyCode, $"The server replied {code}: {message}", null)
            : RemoteFailure(message);

    /// <summary>本库检查出的请求错误：路径为空、上传的本地源文件不存在、取值未定义（不发送，P63-D5）。</summary>
    /// <param name="message">写明具体哪一项非法。</param>
    /// <returns><see cref="CommErrorKind.InvalidRequest"/> 的结果。</returns>
    public static CommResult InvalidRequest(string message) => CommResult.Fail(message, CommErrorKind.InvalidRequest);

    /// <summary>本库在服务器上检查出的"不存在"（P63-D8、D9、D10）：RemoteError，没有应答码。</summary>
    /// <param name="message">写明哪个远程对象不存在。</param>
    /// <returns><see cref="CommErrorKind.RemoteError"/> 的结果。</returns>
    public static CommResult RemoteObjectMissing(string message) => CommResult.Fail(message, CommErrorKind.RemoteError);

    /// <summary>远程失败但没有应答码：SDK 报告的失败，或本库检查出的失败（例如路径是文件而不是目录）。RemoteError。</summary>
    /// <param name="message">写明失败的内容。</param>
    /// <returns><see cref="CommErrorKind.RemoteError"/> 的结果。</returns>
    public static CommResult RemoteFailure(string message) => CommResult.Fail(message, CommErrorKind.RemoteError);

    /// <summary>本地文件系统错误（无法创建本地目录、写入或替换临时文件）：Unspecified（P63 第 16 节）。</summary>
    /// <param name="exception">本地文件操作抛出的异常。</param>
    /// <returns><see cref="CommErrorKind.Unspecified"/> 的结果。</returns>
    public static CommResult LocalFileSystem(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Fail(CommErrorKind.Unspecified, null, $"The local file operation failed: {exception.Message}", exception);
    }

    /// <summary>调用方取消（包括在串行门前排队时取消）：Cancelled。</summary>
    /// <returns><see cref="CommErrorKind.Cancelled"/> 的结果。</returns>
    public static CommResult Cancelled() => CommResult.Fail("The operation was cancelled.", CommErrorKind.Cancelled);

    /// <summary>释放之后的操作（P6-C8）：ConnectionClosed。</summary>
    /// <returns><see cref="CommErrorKind.ConnectionClosed"/> 的结果。</returns>
    public static CommResult Disposed() => CommResult.Fail("The FTP client has been disposed.", CommErrorKind.ConnectionClosed);

    // 应答码：530 是登录失败；421、425、426 是连接或数据连接失败；452、552 是存储空间不足；
    // 502、504 是命令未实现；其余 4xx、5xx（含 550、553）是远程错误。
    private static CommResult FromReplyCode(string? completionCode, string text, Exception? exception)
    {
        long? code = ParseCode(completionCode);
        CommErrorKind kind = code switch
        {
            530 => CommErrorKind.AuthenticationFailed,
            421 or 425 or 426 => CommErrorKind.ConnectionClosed,
            452 or 552 => CommErrorKind.ResourceExhausted,
            502 or 504 => CommErrorKind.NotSupported,
            _ => CommErrorKind.RemoteError,
        };

        return Fail(kind, code, text, exception);
    }

    // 没有应答码的运行期失败：取消（释放时进行中的操作）、超时、连接失败，以及释放之后的访问。
    // SDK 常把套接字异常包在 FtpException 里（例如上传途中断线），因此沿内层异常链逐层判断，外层的异常先匹配。
    private static CommResult FromTransport(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return Fail(CommErrorKind.ConnectionClosed, null, "The operation was aborted because the client was disposed or the connection was closed.", exception);
            }

            if (IsTimeout(current))
            {
                return Fail(CommErrorKind.Timeout, null, "The FTP operation timed out.", exception);
            }

            if (current is ObjectDisposedException)
            {
                return Fail(CommErrorKind.ConnectionClosed, null, "The FTP client has been disposed.", exception);
            }

            if (current is SocketException or IOException)
            {
                return Fail(CommErrorKind.ConnectionClosed, null, $"The connection failed: {exception.Message}", exception);
            }
        }

        return Fail(CommErrorKind.Unspecified, null, $"The FTP operation failed with {exception.GetType().Name}: {exception.Message}", exception);
    }

    private static FtpCommandException? FindCommandException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is FtpCommandException command)
            {
                return command;
            }
        }

        return null;
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
