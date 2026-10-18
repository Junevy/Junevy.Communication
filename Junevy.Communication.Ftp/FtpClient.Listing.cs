using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp;

// 列目录与存在性检查（P63-D9、D10）。SDK 对"不存在"的处理不一致，见第 18.2 节：
// 列目录静默返回空列表（本库再检查一次目录），文件存在性与目录存在性返回 false，大小返回 -1（本库转换为失败）。
public sealed partial class FtpClient
{
    /// <summary>
    /// 列出远程目录的条目，不含 <c>.</c> 与 <c>..</c>。服务器支持 MLST 时由 SDK 用 MLSD，否则解析 LIST 的输出（P63-D9）。
    /// </summary>
    /// <param name="remoteDirectory">远程目录路径，绝对或相对于登录目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目列表；目录不存在时失败（RemoteError，没有应答码）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="remoteDirectory"/> 为 null。</exception>
    public Task<CommResult<IReadOnlyList<FtpEntry>>> ListAsync(string remoteDirectory, CancellationToken cancellationToken = default) =>
        PathOperationAsync<IReadOnlyList<FtpEntry>>(
            remoteDirectory,
            nameof(remoteDirectory),
            async (session, path, token) =>
            {
                IReadOnlyList<FtpEntry> entries = await session.ListAsync(path, token).ConfigureAwait(false);

                // 列表为空时再确认目录确实存在，区分"空目录"与"不存在"（P63-D9）。
                if (entries.Count > 0 || await session.DirectoryExistsAsync(path, token).ConfigureAwait(false))
                {
                    return CommResult<IReadOnlyList<FtpEntry>>.Success(entries);
                }

                return FtpErrorMap.RemoteObjectMissing($"The remote directory does not exist: {path}")
                    .As<IReadOnlyList<FtpEntry>>();
            },
            cancellationToken);

    /// <summary>远程文件是否存在。不存在不是错误，返回 false。</summary>
    /// <param name="remotePath">远程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在与否。</returns>
    public Task<CommResult<bool>> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default) =>
        PathOperationAsync<bool>(
            remotePath,
            nameof(remotePath),
            async (session, path, token) =>
                CommResult<bool>.Success(await session.FileExistsAsync(path, token).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>远程目录是否存在。不存在不是错误，返回 false。</summary>
    /// <param name="remotePath">远程目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在与否。</returns>
    public Task<CommResult<bool>> DirectoryExistsAsync(string remotePath, CancellationToken cancellationToken = default) =>
        PathOperationAsync<bool>(
            remotePath,
            nameof(remotePath),
            async (session, path, token) =>
                CommResult<bool>.Success(await session.DirectoryExistsAsync(path, token).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>远程文件的大小（字节）。</summary>
    /// <param name="remotePath">远程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>大小；文件不存在或服务器不报告大小时失败（RemoteError，没有应答码）。</returns>
    public Task<CommResult<long>> GetFileSizeAsync(string remotePath, CancellationToken cancellationToken = default) =>
        PathOperationAsync<long>(
            remotePath,
            nameof(remotePath),
            async (session, path, token) =>
            {
                long? size = await session.GetFileSizeAsync(path, token).ConfigureAwait(false);
                return size is { } value
                    ? CommResult<long>.Success(value)
                    : FtpErrorMap.RemoteObjectMissing(
                        $"The size of {path} is not reported: the file does not exist or the server does not report sizes.")
                        .As<long>();
            },
            cancellationToken);
}
