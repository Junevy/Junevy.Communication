using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Ftp;

// 文件与目录操作（计划 Task 4）。当前为占位：返回 NotSupported，Task 4 替换为真实实现。
public sealed partial class FtpClient
{
    /// <summary>删除远程文件；文件不存在时失败。</summary>
    /// <param name="remotePath">远程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除结果。</returns>
    public Task<CommResult> DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>逐级创建远程目录；已经存在算成功。</summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建结果。</returns>
    public Task<CommResult> CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>递归删除远程目录（包含文件与子目录）；目录不存在时失败。</summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除结果。</returns>
    public Task<CommResult> DeleteDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>重命名或移动远程条目；源不存在时失败。</summary>
    /// <param name="remotePath">源路径。</param>
    /// <param name="newRemotePath">目标路径；已存在时的行为由服务器决定。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重命名结果。</returns>
    public Task<CommResult> RenameAsync(string remotePath, string newRemotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult.Fail("Not implemented yet.", CommErrorKind.NotSupported));
}
