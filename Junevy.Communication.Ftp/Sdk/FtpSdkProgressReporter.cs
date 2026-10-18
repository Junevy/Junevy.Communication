using SdkProgress = FluentFTP.FtpProgress;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// 把 SDK 的进度报告转换为本库的进度并转发给调用方（P63-D11）。
/// 报告在 SDK 的回调线程上发出，README 写明这一点；这里不做任何线程切换。
/// </summary>
internal sealed class FtpSdkProgressReporter : IProgress<SdkProgress>
{
    private readonly IProgress<FtpTransferProgress> target;

    /// <summary>创建转发器。</summary>
    /// <param name="target">调用方的进度接收者。</param>
    public FtpSdkProgressReporter(IProgress<FtpTransferProgress> target)
    {
        this.target = target;
    }

    /// <summary>转换并转发一次进度报告。</summary>
    /// <param name="value">SDK 的进度。</param>
    public void Report(SdkProgress value) => target.Report(FtpSdkProgressMap.ToTransferProgress(value));
}
