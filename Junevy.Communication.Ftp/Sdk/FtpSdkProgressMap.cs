using SdkProgress = FluentFTP.FtpProgress;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// SDK 的传输进度（<c>FtpProgress</c>）到 <see cref="FtpTransferProgress"/> 的映射（P63-D11）。
/// SDK 用 -1 表示进度不确定（总长未知），这里转换为 null，不让哨兵进入公开类型。
/// </summary>
internal static class FtpSdkProgressMap
{
    /// <summary>映射一次进度报告。</summary>
    /// <param name="progress">SDK 报告的进度；不能为 null。</param>
    /// <returns>本库的进度。</returns>
    public static FtpTransferProgress ToTransferProgress(SdkProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        double? percent = progress.Progress < 0 ? null : progress.Progress;
        return new FtpTransferProgress(progress.TransferredBytes, percent);
    }
}
