using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp;

// 上传与下载（P63-D5–D7、D11）。参数先全部检查 null，再检查取值；本地的前置条件在连接之前检查：
// 本地源文件不存在、目标路径为空，都不会连接服务器。
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
        ArgumentNullException.ThrowIfNull(localPath);
        ArgumentNullException.ThrowIfNull(remotePath);

        if (FtpRequests.CheckLocalSource(localPath, nameof(localPath)) is { } invalidSource)
        {
            return invalidSource.As<FtpTransferStatus>();
        }

        if (FtpRequests.CheckExistsMode(existsMode, nameof(existsMode)) is { } invalidMode)
        {
            return invalidMode.As<FtpTransferStatus>();
        }

        return await PathOperationAsync<FtpTransferStatus>(
            "UploadFile",
            remotePath,
            nameof(remotePath),
            async (session, operation, token) =>
            {
                CommResult<FtpTransferStatus> result = await session
                    .UploadFileAsync(localPath, operation.Target, existsMode, progress, token)
                    .ConfigureAwait(false);
                operation.TransferredBytes = result is { IsSuccess: true, Data: FtpTransferStatus.Transferred }
                    ? new FileInfo(localPath).Length
                    : 0;
                return result;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从流上传到远程路径。从流的当前位置读到末尾；库不释放调用方的流。可定位的流能报告百分比。
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
        ArgumentNullException.ThrowIfNull(remotePath);

        if (FtpRequests.CheckExistsMode(existsMode, nameof(existsMode)) is { } invalidMode)
        {
            return invalidMode.As<FtpTransferStatus>();
        }

        return await PathOperationAsync<FtpTransferStatus>(
            "Upload",
            remotePath,
            nameof(remotePath),
            async (session, operation, token) =>
            {
                var view = new FtpStreamView(source);
                CommResult<FtpTransferStatus> result = await session
                    .UploadStreamAsync(view, operation.Target, existsMode, progress, token)
                    .ConfigureAwait(false);
                operation.TransferredBytes = view.TransferredBytes;
                return result;
            },
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
    /// <returns>传输结果；客户端已释放时为 ConnectionClosed，不检查也不改动本地文件。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> 或 <paramref name="localPath"/> 为 null。</exception>
    public async Task<CommResult<FtpTransferStatus>> DownloadFileAsync(
        string remotePath,
        string localPath,
        FtpExistsMode existsMode = FtpExistsMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        ArgumentNullException.ThrowIfNull(localPath);

        if ((FtpRequests.CheckPath(remotePath, nameof(remotePath))
            ?? FtpRequests.CheckLocalTarget(localPath, nameof(localPath))
            ?? FtpRequests.CheckExistsMode(existsMode, nameof(existsMode))) is { } invalid)
        {
            return invalid.As<FtpTransferStatus>();
        }

        // 释放之后不做任何本地检查或改动（P6-C8）：跳过与创建父目录都发生在这之后。
        if (lifecycle.IsDisposed)
        {
            return FtpErrorMap.Disposed().As<FtpTransferStatus>();
        }

        if (existsMode == FtpExistsMode.Skip && File.Exists(localPath))
        {
            return CommResult<FtpTransferStatus>.Success(FtpTransferStatus.Skipped);
        }

        if (FtpLocalFile.EnsureParentDirectory(localPath) is { } directoryError)
        {
            return directoryError.As<FtpTransferStatus>();
        }

        // 替换目标也在操作之内，因此它的失败同样进入操作日志；临时文件的清理在门外进行，SDK 抛出的异常同样覆盖。
        string temporary = FtpLocalFile.TemporaryPathFor(localPath);
        CommResult<FtpTransferStatus> transfer = await PathOperationAsync<FtpTransferStatus>(
            "DownloadFile",
            remotePath,
            nameof(remotePath),
            async (session, operation, token) =>
            {
                CommResult<FtpTransferStatus> downloaded = await session
                    .DownloadFileAsync(temporary, operation.Target, progress, token)
                    .ConfigureAwait(false);
                if (!downloaded.IsSuccess)
                {
                    return downloaded;
                }

                operation.TransferredBytes = new FileInfo(temporary).Length;
                return FtpLocalFile.Commit(temporary, localPath);
            },
            cancellationToken).ConfigureAwait(false);

        if (!transfer.IsSuccess)
        {
            FtpLocalFile.TryDelete(temporary);
        }

        return transfer;
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
        ArgumentNullException.ThrowIfNull(remotePath);
        ArgumentNullException.ThrowIfNull(destination);

        CommResult<FtpTransferStatus> result = await PathOperationAsync<FtpTransferStatus>(
            "Download",
            remotePath,
            nameof(remotePath),
            async (session, operation, token) =>
            {
                var view = new FtpStreamView(destination);
                CommResult<FtpTransferStatus> downloaded = await session
                    .DownloadStreamAsync(view, operation.Target, progress, token)
                    .ConfigureAwait(false);
                operation.TransferredBytes = view.TransferredBytes;
                return downloaded;
            },
            cancellationToken).ConfigureAwait(false);
        return result.ToResult();
    }
}
