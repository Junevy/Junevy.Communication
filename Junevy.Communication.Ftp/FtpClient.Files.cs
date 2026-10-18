using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp;

// 文件与目录操作（P63-D8）。不存在的文件、目录与源路径由 SDK 以 550 抛出，结果带应答码。
public sealed partial class FtpClient
{
    /// <summary>删除远程文件；文件不存在时失败（RemoteError，应答码 550）。</summary>
    /// <param name="remotePath">远程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除结果。</returns>
    public Task<CommResult> DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) =>
        PathActionAsync("DeleteFile", remotePath, nameof(remotePath), (session, path, token) => session.DeleteFileAsync(path, token), cancellationToken);

    /// <summary>
    /// 逐级创建远程目录。已经存在的目录算成功；服务器拒绝创建（例如权限不足，或路径上是文件）时失败（RemoteError，带服务器的应答码）。
    /// </summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建结果。</returns>
    public async Task<CommResult> CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        CommResult<bool> result = await PathOperationAsync<bool>(
            "CreateDirectory",
            remotePath,
            nameof(remotePath),
            async (session, operation, token) =>
            {
                string path = operation.Target;
                if (await session.CreateDirectoryAsync(path, token).ConfigureAwait(false))
                {
                    return CommResult<bool>.Success(true);
                }

                // SDK 对 MKD 的任何 550 都返回 false（已存在、权限不足、路径上是文件）：先记下 MKD 的应答码，
                // 再确认路径是不是已经存在的目录。
                string? refusal = session.LastReplyCode;
                if (await session.DirectoryExistsAsync(path, token).ConfigureAwait(false))
                {
                    return CommResult<bool>.Success(true);
                }

                return FtpErrorMap.LastReplyFailure(refusal, $"The server refused to create the directory {path}, or a file is in the way.")
                    .As<bool>();
            },
            cancellationToken).ConfigureAwait(false);
        return result.ToResult();
    }

    /// <summary>递归删除远程目录（包含文件与子目录）；目录不存在时失败（RemoteError，应答码 550）。</summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除结果。</returns>
    public Task<CommResult> DeleteDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
        PathActionAsync("DeleteDirectory", remotePath, nameof(remotePath), (session, path, token) => session.DeleteDirectoryAsync(path, token), cancellationToken);

    /// <summary>重命名或移动远程条目。源不存在时失败（RemoteError，应答码 550）；目标已存在时的行为由服务器决定。</summary>
    /// <param name="remotePath">源路径。</param>
    /// <param name="newRemotePath">目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重命名结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> 或 <paramref name="newRemotePath"/> 为 null。</exception>
    public async Task<CommResult> RenameAsync(string remotePath, string newRemotePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        ArgumentNullException.ThrowIfNull(newRemotePath);

        if (FtpRequests.CheckPath(newRemotePath, nameof(newRemotePath)) is { } invalid)
        {
            return invalid;
        }

        return await PathActionAsync(
            "Rename",
            remotePath,
            nameof(remotePath),
            (session, path, token) => session.RenameAsync(path, newRemotePath, token),
            cancellationToken).ConfigureAwait(false);
    }
}
