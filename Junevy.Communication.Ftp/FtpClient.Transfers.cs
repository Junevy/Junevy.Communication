using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Ftp;

// 上传与下载（计划 Task 5）。当前为占位：返回 NotSupported，Task 5 替换为真实实现。
public sealed partial class FtpClient
{
    /// <summary>把本地文件上传到远程路径。</summary>
    /// <param name="localPath">本地源文件；不存在时返回 InvalidRequest 且不连接服务器。</param>
    /// <param name="remotePath">远程目标路径。</param>
    /// <param name="existsMode">目标已存在时的处理方式。</param>
    /// <param name="progress">进度报告；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输状态。</returns>
    public Task<CommResult<FtpTransferStatus>> UploadFileAsync(
        string localPath,
        string remotePath,
        FtpExistsMode existsMode = FtpExistsMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<FtpTransferStatus>.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>从流上传到远程路径；从流的当前位置读到末尾，库不释放调用方的流。</summary>
    /// <param name="source">源流。</param>
    /// <param name="remotePath">远程目标路径。</param>
    /// <param name="existsMode">目标已存在时的处理方式。</param>
    /// <param name="progress">进度报告；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输状态。</returns>
    public Task<CommResult<FtpTransferStatus>> UploadAsync(
        Stream source,
        string remotePath,
        FtpExistsMode existsMode = FtpExistsMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<FtpTransferStatus>.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>把远程文件下载到本地路径；先下载到临时文件，成功后再替换目标（原子，P63-D7）。</summary>
    /// <param name="remotePath">远程源文件。</param>
    /// <param name="localPath">本地目标路径；父目录不存在时自动创建。</param>
    /// <param name="existsMode">目标已存在时的处理方式。</param>
    /// <param name="progress">进度报告；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输状态。</returns>
    public Task<CommResult<FtpTransferStatus>> DownloadFileAsync(
        string remotePath,
        string localPath,
        FtpExistsMode existsMode = FtpExistsMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<FtpTransferStatus>.Fail("Not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>把远程文件下载到流；从流的当前位置开始写，库不释放调用方的流。</summary>
    /// <param name="remotePath">远程源文件。</param>
    /// <param name="destination">目标流。</param>
    /// <param name="progress">进度报告；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    public Task<CommResult> DownloadAsync(
        string remotePath,
        Stream destination,
        IProgress<FtpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult.Fail("Not implemented yet.", CommErrorKind.NotSupported));
}
