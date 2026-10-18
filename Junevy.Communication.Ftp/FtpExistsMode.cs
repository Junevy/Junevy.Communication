namespace Junevy.Communication.Ftp;

/// <summary>
/// 目标已存在时的处理方式（P63-D6）：上传看远程文件，下载看本地文件。不提供续传与追加（计划第 19 节）。
/// </summary>
public enum FtpExistsMode
{
    /// <summary>覆盖已有的目标。</summary>
    Overwrite,

    /// <summary>目标已存在时跳过：结果成功，值为 <see cref="FtpTransferStatus.Skipped"/>，目标不变。</summary>
    Skip,
}
