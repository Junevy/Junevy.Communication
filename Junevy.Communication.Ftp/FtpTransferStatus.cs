namespace Junevy.Communication.Ftp;

/// <summary>传输的结果状态（P63-D6）。</summary>
public enum FtpTransferStatus
{
    /// <summary>已传输。</summary>
    Transferred,

    /// <summary>目标已存在且模式为 <see cref="FtpExistsMode.Skip"/>，没有传输。</summary>
    Skipped,
}
