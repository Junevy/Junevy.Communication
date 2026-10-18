using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Microsoft.AspNetCore.Connections;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 启动失败的分类（内部，P32-D12）：把 Kestrel 绑定地址时的异常翻译为通道族的错误分类，使用方只需判断 <see cref="CommErrorKind"/>。
/// </summary>
internal static class StartFailure
{
    /// <summary>把启动异常翻译为 <see cref="CommResult"/>。</summary>
    /// <param name="exception">启动时抛出的异常。</param>
    /// <returns>失败结果。</returns>
    public static CommResult Classify(Exception exception)
    {
        if (exception is OperationCanceledException)
            return CommResult.Fail("The server start was cancelled.", CommErrorKind.Cancelled, exception: exception);

        if (exception is AddressInUseException || exception.InnerException is AddressInUseException)
            return CommResult.Fail("The server address is already in use by another listener.", CommErrorKind.ResourceExhausted, exception: exception);

        if (IsAccessDenied(exception))
            return CommResult.Fail("The server address is reserved by the system or needs elevated permission.", CommErrorKind.AuthenticationFailed, exception: exception);

        return CommResult.Fail("The server could not start: " + exception.Message, CommErrorKind.Unspecified, exception: exception);
    }

    private static bool IsAccessDenied(Exception exception)
    {
        Exception? current = exception;
        while (current != null)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AccessDenied })
                return true;
            current = current.InnerException;
        }

        return false;
    }
}
