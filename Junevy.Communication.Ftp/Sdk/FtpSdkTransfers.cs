using Junevy.Communication.Core.Results;
using SdkRemoteExists = FluentFTP.FtpRemoteExists;
using SdkStatus = FluentFTP.FtpStatus;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// SDK 的传输状态与存在性策略到本库类型的映射（P63-D6、D7）。失败时：SDK 抛出的异常由生命周期经 <see cref="FtpErrorMap"/> 翻译；
/// SDK 只返回 Failed 或 false 的（例如下载中断）则按最后一条应答码分类。
/// </summary>
internal static class FtpSdkTransfers
{
    /// <summary>目标已存在时的 SDK 策略。</summary>
    /// <param name="mode">本库的处理方式。</param>
    /// <returns>SDK 的策略。</returns>
    public static SdkRemoteExists ToRemoteExists(FtpExistsMode mode) => mode switch
    {
        FtpExistsMode.Skip => SdkRemoteExists.Skip,
        _ => SdkRemoteExists.Overwrite,
    };

    /// <summary>上传或下载到文件的结果。</summary>
    /// <param name="status">SDK 的状态。</param>
    /// <param name="lastReplyCode">SDK 最后一条应答的代码，可能为 null。</param>
    /// <returns>传输结果。</returns>
    public static CommResult<FtpTransferStatus> ToResult(SdkStatus status, string? lastReplyCode) => status switch
    {
        SdkStatus.Success => CommResult<FtpTransferStatus>.Success(FtpTransferStatus.Transferred),
        SdkStatus.Skipped => CommResult<FtpTransferStatus>.Success(FtpTransferStatus.Skipped),
        _ => FtpErrorMap.TransferFailed(lastReplyCode, "The transfer failed.").As<FtpTransferStatus>(),
    };

    /// <summary>下载到流的结果：SDK 返回 false 表示传输未完成。</summary>
    /// <param name="completed">SDK 报告的完成情况。</param>
    /// <param name="lastReplyCode">SDK 最后一条应答的代码，可能为 null。</param>
    /// <returns>传输结果。</returns>
    public static CommResult<FtpTransferStatus> ToStreamResult(bool completed, string? lastReplyCode) =>
        completed
            ? CommResult<FtpTransferStatus>.Success(FtpTransferStatus.Transferred)
            : FtpErrorMap.TransferFailed(lastReplyCode, "The transfer failed.").As<FtpTransferStatus>();
}
