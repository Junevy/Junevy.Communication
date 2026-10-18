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
        PathActionAsync(remotePath, nameof(remotePath), (session, path, token) => session.DeleteFileAsync(path, token), cancellationToken);

    /// <summary>逐级创建远程目录。已经存在的目录算成功；路径上是文件则失败（RemoteError）。</summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建结果。</returns>
    public async Task<CommResult> CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        CommResult<bool> result = await PathOperationAsync<bool>(
            remotePath,
            nameof(remotePath),
            async (session, path, token) =>
            {
                // SDK 对已存在的目录返回 false：只要路径确实是目录就算成功。
                if (await session.CreateDirectoryAsync(path, token).ConfigureAwait(false)
                    || await session.DirectoryExistsAsync(path, token).ConfigureAwait(false))
                {
                    return CommResult<bool>.Success(true);
                }

                return FtpErrorMap.RemoteFailure($"The path exists and is not a directory: {path}").As<bool>();
            },
            cancellationToken).ConfigureAwait(false);
        return result.ToResult();
    }

    /// <summary>递归删除远程目录（包含文件与子目录）；目录不存在时失败（RemoteError，应答码 550）。</summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除结果。</returns>
    public Task<CommResult> DeleteDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
        PathActionAsync(remotePath, nameof(remotePath), (session, path, token) => session.DeleteDirectoryAsync(path, token), cancellationToken);

    /// <summary>重命名或移动远程条目。源不存在时失败（RemoteError，应答码 550）；目标已存在时的行为由服务器决定。</summary>
    /// <param name="remotePath">源路径。</param>
    /// <param name="newRemotePath">目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重命名结果。</returns>
    public async Task<CommResult> RenameAsync(string remotePath, string newRemotePath, CancellationToken cancellationToken = default)
    {
        if (FtpRequests.CheckPath(newRemotePath, nameof(newRemotePath)) is { } invalid)
        {
            return invalid;
        }

        return await PathActionAsync(
            remotePath,
            nameof(remotePath),
            (session, path, token) => session.RenameAsync(path, newRemotePath, token),
            cancellationToken).ConfigureAwait(false);
    }
}
