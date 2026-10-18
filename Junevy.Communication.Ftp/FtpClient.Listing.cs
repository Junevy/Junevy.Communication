using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Ftp;

// 列目录与存在性检查（计划 Task 4）。当前为占位：返回 NotSupported，Task 4 替换为真实实现。
public sealed partial class FtpClient
{
    /// <summary>列出远程目录的条目（不含 <c>.</c> 与 <c>..</c>）。</summary>
    /// <param name="remoteDirectory">远程目录路径，绝对或相对于登录目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目列表；目录不存在时失败。</returns>
    public Task<CommResult<IReadOnlyList<FtpEntry>>> ListAsync(string remoteDirectory, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<IReadOnlyList<FtpEntry>>.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>远程文件是否存在。</summary>
    /// <param name="remotePath">远程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在与否。</returns>
    public Task<CommResult<bool>> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<bool>.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>远程目录是否存在。</summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在与否。</returns>
    public Task<CommResult<bool>> DirectoryExistsAsync(string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<bool>.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>远程文件的大小（字节）。</summary>
    /// <param name="remotePath">远程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>大小；文件不存在或服务器不提供大小时失败。</returns>
    public Task<CommResult<long>> GetFileSizeAsync(string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<long>.Fail("Not implemented yet.", CommErrorKind.NotSupported));
}
