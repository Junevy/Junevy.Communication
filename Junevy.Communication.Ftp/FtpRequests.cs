using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp;

/// <summary>
/// 请求参数的检查（P63-D5）：null 是编程错误，抛 <see cref="ArgumentNullException"/>；空或全空白是请求错误，
/// 返回 <see cref="CommErrorKind.InvalidRequest"/> 且不连接服务器。
/// </summary>
internal static class FtpRequests
{
    /// <summary>检查远程路径。</summary>
    /// <param name="path">路径；为 null 时抛异常。</param>
    /// <param name="parameter">参数名，用于异常与错误消息。</param>
    /// <returns>路径合法时为 null；否则为 InvalidRequest 的结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null。</exception>
    public static CommResult? CheckPath(string? path, string parameter)
    {
        ArgumentNullException.ThrowIfNull(path, parameter);

        return string.IsNullOrWhiteSpace(path)
            ? FtpErrorMap.InvalidRequest($"{parameter} must not be empty.")
            : null;
    }
}
