using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp;

/// <summary>
/// 请求参数的检查（P63-D5）：null 是编程错误，抛 <see cref="ArgumentNullException"/>；空或全空白，以及上传的本地源文件不存在，
/// 是请求错误，返回 <see cref="CommErrorKind.InvalidRequest"/> 且不连接服务器。
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

    /// <summary>检查上传的本地源文件：必须存在。</summary>
    /// <param name="localPath">本地文件路径；为 null 时抛异常。</param>
    /// <param name="parameter">参数名。</param>
    /// <returns>文件存在时为 null；否则为 InvalidRequest 的结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="localPath"/> 为 null。</exception>
    public static CommResult? CheckLocalSource(string? localPath, string parameter)
    {
        ArgumentNullException.ThrowIfNull(localPath, parameter);

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return FtpErrorMap.InvalidRequest($"{parameter} must not be empty.");
        }

        return File.Exists(localPath)
            ? null
            : FtpErrorMap.InvalidRequest($"The local source file does not exist: {localPath}");
    }

    /// <summary>检查下载的本地目标：只要求非空；父目录在下载时创建。</summary>
    /// <param name="localPath">本地文件路径；为 null 时抛异常。</param>
    /// <param name="parameter">参数名。</param>
    /// <returns>合法时为 null；否则为 InvalidRequest 的结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="localPath"/> 为 null。</exception>
    public static CommResult? CheckLocalTarget(string? localPath, string parameter)
    {
        ArgumentNullException.ThrowIfNull(localPath, parameter);

        return string.IsNullOrWhiteSpace(localPath)
            ? FtpErrorMap.InvalidRequest($"{parameter} must not be empty.")
            : null;
    }

    /// <summary>检查目标已存在时的处理方式：未定义的取值返回 InvalidRequest（不抛异常，P6-C8）。</summary>
    /// <param name="mode">处理方式。</param>
    /// <param name="parameter">参数名。</param>
    /// <returns>合法时为 null；否则为 InvalidRequest 的结果。</returns>
    public static CommResult? CheckExistsMode(FtpExistsMode mode, string parameter) =>
        Enum.IsDefined(mode) ? null : FtpErrorMap.InvalidRequest($"{parameter} has a value that is not defined.");
}
