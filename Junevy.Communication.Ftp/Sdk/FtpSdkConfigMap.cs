using Junevy.Communication.Ftp;
using SdkConfig = FluentFTP.FtpConfig;
using SdkDataConnectionType = FluentFTP.FtpDataConnectionType;
using SdkDataType = FluentFTP.FtpDataType;
using SdkDate = FluentFTP.FtpDate;
using SdkEncryptionMode = FluentFTP.FtpEncryptionMode;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// 连接设置到 FluentFTP 配置的映射（P63-D2、D14、D15）。计划要求的取值全部显式写出，不依赖 SDK 的版本默认：
/// 被动数据连接、只用二进制传输、不发 NOOP 心跳、关闭 SDK 的自动重试（RetryAttempts = 1）、日志全部脱敏。
/// </summary>
internal static class FtpSdkConfigMap
{
    /// <summary>映射为 SDK 的配置对象。</summary>
    /// <param name="settings">连接设置。</param>
    /// <returns>新的 SDK 配置。</returns>
    public static SdkConfig ToConfig(FtpConnectionSettings settings) => new()
    {
        EncryptionMode = ToEncryptionMode(settings.Encryption),
        DataConnectionEncryption = settings.Encryption != FtpEncryption.None,
        ValidateAnyCertificate = settings.AllowUntrustedCertificates,
        DataConnectionType = SdkDataConnectionType.AutoPassive,
        UploadDataType = SdkDataType.Binary,
        DownloadDataType = SdkDataType.Binary,
        ListingDataType = SdkDataType.Binary,
        RetryAttempts = 1,
        Noop = false,
        DisconnectWithQuit = true,
        CheckCapabilities = true,
        TimeConversion = SdkDate.ServerTime,
        ConnectTimeout = settings.ConnectTimeout,
        ReadTimeout = settings.ReadTimeout,
        WriteTimeout = settings.ReadTimeout,
        DataConnectionConnectTimeout = settings.DataConnectionTimeout,
        DataConnectionReadTimeout = settings.DataConnectionTimeout,
        DataConnectionWriteTimeout = settings.DataConnectionTimeout,
        LogToConsole = false,
        LogHost = false,
        LogUserName = false,
        LogPassword = false,
    };

    private static SdkEncryptionMode ToEncryptionMode(FtpEncryption encryption) => encryption switch
    {
        FtpEncryption.Explicit => SdkEncryptionMode.Explicit,
        FtpEncryption.Implicit => SdkEncryptionMode.Implicit,
        _ => SdkEncryptionMode.None,
    };
}
