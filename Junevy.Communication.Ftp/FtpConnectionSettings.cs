namespace Junevy.Communication.Ftp;

/// <summary>
/// 通过校验的连接设置快照：创建客户端时从选项复制一份，之后修改选项对象不影响已创建的客户端（P63 Task 3）。
/// </summary>
internal sealed record FtpConnectionSettings(
    string Host,
    int Port,
    string? UserName,
    string? Password,
    FtpEncryption Encryption,
    bool AllowUntrustedCertificates,
    string? EncodingName,
    int ConnectTimeout,
    int ReadTimeout,
    int DataConnectionTimeout)
{
    /// <summary>从已通过校验的选项取快照。</summary>
    /// <param name="options">选项；调用方必须先调用 <see cref="FtpClientOptionsValidator.Validate"/>。</param>
    /// <returns>快照。</returns>
    public static FtpConnectionSettings From(FtpClientOptions options) => new(
        Host: options.Host ?? string.Empty,
        Port: options.Port,
        UserName: options.UserName,
        Password: options.Password,
        Encryption: options.Encryption,
        AllowUntrustedCertificates: options.AllowUntrustedCertificates,
        EncodingName: options.EncodingName,
        ConnectTimeout: options.ConnectTimeout,
        ReadTimeout: options.ReadTimeout,
        DataConnectionTimeout: options.DataConnectionTimeout);
}
