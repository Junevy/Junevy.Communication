namespace Junevy.Communication.Ftp;

/// <summary>
/// 一次传输的进度（不可变）。在 SDK 的回调线程上报告（P63-D11）。
/// </summary>
public sealed class FtpTransferProgress
{
    internal FtpTransferProgress(long transferredBytes, double? percent)
    {
        TransferredBytes = transferredBytes;
        Percent = percent;
    }

    /// <summary>已传输的字节数。</summary>
    public long TransferredBytes { get; }

    /// <summary>完成百分比（0–100）；总长未知时为 null。</summary>
    public double? Percent { get; }
}
