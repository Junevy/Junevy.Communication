using System.Net;
using System.Text;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;
using SdkFtpClient = FluentFTP.AsyncFtpClient;
using SdkListItem = FluentFTP.FtpListItem;
using SdkListOption = FluentFTP.FtpListOption;
using SdkLocalExists = FluentFTP.FtpLocalExists;
using SdkProgress = FluentFTP.FtpProgress;
using SdkStatus = FluentFTP.FtpStatus;
using SdkVerify = FluentFTP.FtpVerify;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// FluentFTP 客户端的包装（P6-C5）：只持有 SDK 的一个客户端，对外只暴露 BCL 与本库的类型。
/// SDK 的异常原样抛出，由 <see cref="FtpLifecycle"/> 经 <see cref="FtpErrorMap"/> 翻译。
/// </summary>
internal sealed class FtpSdkSession : IAsyncDisposable
{
    private readonly SdkFtpClient client;

    /// <summary>创建 SDK 客户端（不连接）。</summary>
    /// <param name="settings">已通过校验的连接设置。</param>
    /// <param name="loggerFactory">日志工厂；SDK 的日志经 <see cref="FtpSdkLogBridge"/> 进入它。</param>
    public FtpSdkSession(FtpConnectionSettings settings, ILoggerFactory loggerFactory)
    {
        FluentFTP.FtpConfig config = FtpSdkConfigMap.ToConfig(settings);
        var bridge = new FtpSdkLogBridge(loggerFactory);

        // SDK 不接受空的 NetworkCredential：匿名登录使用不带凭据的重载。
        client = settings.UserName is null
            ? new SdkFtpClient(settings.Host, settings.Port, config, bridge)
            : new SdkFtpClient(
                settings.Host,
                new NetworkCredential(settings.UserName, settings.Password ?? string.Empty),
                settings.Port,
                config,
                bridge);

        Encoding? encoding = settings.EncodingName is null ? null : FtpEncodings.Resolve(settings.EncodingName);
        if (encoding is not null)
        {
            client.Encoding = encoding;
        }
    }

    /// <summary>SDK 报告的控制连接状态。</summary>
    public bool IsConnected => client.IsConnected;

    /// <summary>建立控制连接并登录。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时已连接。</returns>
    public Task ConnectAsync(CancellationToken cancellationToken) => client.Connect(cancellationToken);

    /// <summary>确认控制连接仍然可用。SDK 对健康的连接发送一条 NOOP；对已经断开的连接直接返回 false，并更新 <see cref="IsConnected"/>。</summary>
    /// <param name="timeoutMilliseconds">等待 NOOP 应答的时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接仍然可用时为 true。</returns>
    public Task<bool> IsAliveAsync(int timeoutMilliseconds, CancellationToken cancellationToken) =>
        client.IsStillConnected(timeoutMilliseconds, cancellationToken);

    /// <summary>发送 QUIT 并关闭控制连接。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>断开完成。</returns>
    public Task DisconnectAsync(CancellationToken cancellationToken) => client.Disconnect(cancellationToken);

    /// <summary>列出目录：SDK 按服务器的特性选择 MLSD 或 LIST。SDK 对不存在的目录静默返回空列表。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目列表，不含 <c>.</c> 与 <c>..</c>。</returns>
    public async Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        SdkListItem[] items = await client.GetListing(path, SdkListOption.Auto, cancellationToken).ConfigureAwait(false);
        return items.Select(FtpSdkEntryMap.ToEntry).ToList();
    }

    /// <summary>文件是否存在（SDK 用 SIZE 或 MDTM 检查）。不存在时返回 false，不抛异常。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在与否。</returns>
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        client.FileExists(path, cancellationToken);

    /// <summary>目录是否存在（SDK 用 CWD 检查）。不存在时返回 false，不抛异常。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在与否。</returns>
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        client.DirectoryExists(path, cancellationToken);

    /// <summary>文件大小。SDK 在服务器不报告大小或文件不存在时返回 -1，这里转换为 null。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>大小；不可用时为 null。</returns>
    public async Task<long?> GetFileSizeAsync(string path, CancellationToken cancellationToken)
    {
        long size = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
        return size < 0 ? null : size;
    }

    /// <summary>逐级创建目录。SDK 对已存在的目录返回 false。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>SDK 报告是否创建了目录。</returns>
    public Task<bool> CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        client.CreateDirectory(path, true, cancellationToken);

    /// <summary>删除文件。文件不存在时 SDK 抛出 <c>FtpCommandException</c>（550）。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除完成。</returns>
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        client.DeleteFile(path, cancellationToken);

    /// <summary>递归删除目录（包含文件与子目录）。目录不存在时 SDK 抛出 <c>FtpCommandException</c>（550）。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除完成。</returns>
    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        client.DeleteDirectory(path, SdkListOption.Recursive, cancellationToken);

    /// <summary>重命名或移动。源不存在时 SDK 抛出 <c>FtpCommandException</c>（550）。</summary>
    /// <param name="from">源路径。</param>
    /// <param name="to">目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重命名完成。</returns>
    public Task RenameAsync(string from, string to, CancellationToken cancellationToken) =>
        client.Rename(from, to, cancellationToken);

    /// <summary>上传本地文件。SDK 的覆盖策略先删除远程文件再上传（第 18.2 节 F22）。</summary>
    /// <param name="localPath">本地源文件。</param>
    /// <param name="remotePath">远程目标路径。</param>
    /// <param name="existsMode">目标已存在时的处理方式。</param>
    /// <param name="progress">进度接收者；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    public async Task<CommResult<FtpTransferStatus>> UploadFileAsync(
        string localPath,
        string remotePath,
        FtpExistsMode existsMode,
        IProgress<FtpTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        SdkStatus status = await client.UploadFile(
            localPath,
            remotePath,
            FtpSdkTransfers.ToRemoteExists(existsMode),
            false,
            SdkVerify.None,
            Report(progress),
            cancellationToken).ConfigureAwait(false);
        return FtpSdkTransfers.ToResult(status, client.LastReply.Code);
    }

    /// <summary>上传流。SDK 忽略流的当前位置，因此调用方应传入 <see cref="FtpStreamView"/>。</summary>
    /// <param name="source">源流（视图）。</param>
    /// <param name="remotePath">远程目标路径。</param>
    /// <param name="existsMode">目标已存在时的处理方式。</param>
    /// <param name="progress">进度接收者；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    public async Task<CommResult<FtpTransferStatus>> UploadStreamAsync(
        Stream source,
        string remotePath,
        FtpExistsMode existsMode,
        IProgress<FtpTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        SdkStatus status = await client.UploadStream(
            source,
            remotePath,
            FtpSdkTransfers.ToRemoteExists(existsMode),
            false,
            Report(progress),
            cancellationToken).ConfigureAwait(false);
        return FtpSdkTransfers.ToResult(status, client.LastReply.Code);
    }

    /// <summary>下载到本地文件。调用方给出的路径是临时文件，由调用方替换目标（P63-D7）。</summary>
    /// <param name="localPath">本地目标（临时）路径。</param>
    /// <param name="remotePath">远程源文件。</param>
    /// <param name="progress">进度接收者；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    public async Task<CommResult<FtpTransferStatus>> DownloadFileAsync(
        string localPath,
        string remotePath,
        IProgress<FtpTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        SdkStatus status = await client.DownloadFile(
            localPath,
            remotePath,
            SdkLocalExists.Overwrite,
            SdkVerify.None,
            Report(progress),
            cancellationToken).ConfigureAwait(false);
        return FtpSdkTransfers.ToResult(status, client.LastReply.Code);
    }

    /// <summary>下载到流。SDK 从流的当前位置开始写。</summary>
    /// <param name="destination">目标流（视图）。</param>
    /// <param name="remotePath">远程源文件。</param>
    /// <param name="progress">进度接收者；可以为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输结果。</returns>
    public async Task<CommResult<FtpTransferStatus>> DownloadStreamAsync(
        Stream destination,
        string remotePath,
        IProgress<FtpTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        bool completed = await client.DownloadStream(destination, remotePath, 0, Report(progress), cancellationToken, 0)
            .ConfigureAwait(false);
        return FtpSdkTransfers.ToStreamResult(completed, client.LastReply.Code);
    }

    private static IProgress<SdkProgress>? Report(IProgress<FtpTransferProgress>? progress) =>
        progress is null ? null : new FtpSdkProgressReporter(progress);

    /// <summary>释放 SDK 客户端，关闭所有套接字。</summary>
    /// <returns>释放完成。</returns>
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
