using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp;

// 上传与下载（P63-D5–D7、D11）。本地的前置条件在连接之前检查：本地源文件不存在、目标路径为空，都不会连接服务器。
public sealed partial class FtpClient
{
    /// <summary>
    /// 把本地文件上传到远程路径。上传不是原子的：覆盖时 SDK 先删除远程文件再上传，中途失败时远程文件会消失（第 18.2 节 F22）。
    /// </summary>
    /// <param name="localPath">本地源文件；不存在时返回 InvalidRequest，且不连接服务器。</param>
    /// <param name="remotePath">远程目标路径。</param>
    /// <param name="existsMode">远程目标已存在时的处理方式：覆盖，或跳过（结果为 Skipped，远程内容不变）。</param>
    /// <param name="progress">进度报告，在 SDK 的回调线程上调用；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="localPath"/> 或 <paramref name="remotePath"/> 为 null。</exception>
    public async Task<CommResult<FtpTransferStatus>> UploadFileAsync(
        string localPath,
        string remotePath,
        FtpExistsMode existsMode = FtpExistsMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (FtpRequests.CheckLocalSource(localPath, nameof(localPath)) is { } invalidSource)
        {
            return invalidSource.As<FtpTransferStatus>();
        }

        if (FtpRequests.CheckExistsMode(existsMode, nameof(existsMode)) is { } invalidMode)
        {
            return invalidMode.As<FtpTransferStatus>();
        }

        return await PathOperationAsync<FtpTransferStatus>(
            remotePath,
            nameof(remotePath),
            (session, path, token) => session.UploadFileAsync(localPath, path, existsMode, progress, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从流上传到远程路径。从流的当前位置读到末尾；库不释放调用方的流。
    /// </summary>
    /// <param name="source">源流。</param>
    /// <param name="remotePath">远程目标路径。</param>
    /// <param name="existsMode">远程目标已存在时的处理方式。</param>
    /// <param name="progress">进度报告；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 或 <paramref name="remotePath"/> 为 null。</exception>
    public async Task<CommResult<FtpTransferStatus>> UploadAsync(
        Stream source,
        string remotePath,
        FtpExistsMode existsMode = FtpExistsMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (FtpRequests.CheckExistsMode(existsMode, nameof(existsMode)) is { } invalidMode)
        {
            return invalidMode.As<FtpTransferStatus>();
        }

        return await PathOperationAsync<FtpTransferStatus>(
            remotePath,
            nameof(remotePath),
            (session, path, token) => session.UploadStreamAsync(new FtpStreamView(source), path, existsMode, progress, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把远程文件下载到本地路径。先下载到同目录的临时文件，成功后才替换目标：失败时原文件保持不变，目录里也不留下临时文件（P63-D7，Q43）。
    /// </summary>
    /// <param name="remotePath">远程源文件。</param>
    /// <param name="localPath">本地目标路径；父目录不存在时自动创建。</param>
    /// <param name="existsMode">本地目标已存在时的处理方式：覆盖，或跳过（结果为 Skipped，本地内容不变，不连接服务器）。</param>
    /// <param name="progress">进度报告；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> 或 <paramref name="localPath"/> 为 null。</exception>
    public async Task<CommResult<FtpTransferStatus>> DownloadFileAsync(
        string remotePath,
        string localPath,
        FtpExistsMode existsMode = FtpExistsMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (FtpRequests.CheckPath(remotePath, nameof(remotePath)) is { } invalidRemote)
        {
            return invalidRemote.As<FtpTransferStatus>();
        }

        if (FtpRequests.CheckLocalTarget(localPath, nameof(localPath)) is { } invalidTarget)
        {
            return invalidTarget.As<FtpTransferStatus>();
        }

        if (FtpRequests.CheckExistsMode(existsMode, nameof(existsMode)) is { } invalidMode)
        {
            return invalidMode.As<FtpTransferStatus>();
        }

        if (existsMode == FtpExistsMode.Skip && File.Exists(localPath))
        {
            return CommResult<FtpTransferStatus>.Success(FtpTransferStatus.Skipped);
        }

        if (FtpLocalFile.EnsureParentDirectory(localPath) is { } directoryError)
        {
            return directoryError.As<FtpTransferStatus>();
        }

        string temporary = FtpLocalFile.TemporaryPathFor(localPath);
        CommResult<FtpTransferStatus> transfer = await PathOperationAsync<FtpTransferStatus>(
            remotePath,
            nameof(remotePath),
            (session, path, token) => session.DownloadFileAsync(temporary, path, progress, token),
            cancellationToken).ConfigureAwait(false);

        if (!transfer.IsSuccess)
        {
            FtpLocalFile.TryDelete(temporary);
            return transfer;
        }

        return FtpLocalFile.Commit(temporary, localPath);
    }

    /// <summary>
    /// 把远程文件下载到流。从流的当前位置开始写；库不释放调用方的流。
    /// </summary>
    /// <param name="remotePath">远程源文件。</param>
    /// <param name="destination">目标流。</param>
    /// <param name="progress">进度报告；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果；远程文件不存在时失败（RemoteError，应答码 550）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> 或 <paramref name="destination"/> 为 null。</exception>
    public async Task<CommResult> DownloadAsync(
        string remotePath,
        Stream destination,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        CommResult<FtpTransferStatus> result = await PathOperationAsync<FtpTransferStatus>(
            remotePath,
            nameof(remotePath),
            (session, path, token) => session.DownloadStreamAsync(new FtpStreamView(destination), path, progress, token),
            cancellationToken).ConfigureAwait(false);
        return result.ToResult();
    }
}
