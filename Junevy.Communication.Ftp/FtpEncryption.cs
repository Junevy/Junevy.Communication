namespace Junevy.Communication.Ftp;

/// <summary>控制连接的加密方式（P63-D13）。</summary>
public enum FtpEncryption
{
    /// <summary>明文 FTP。</summary>
    None,

    /// <summary>显式 FTPS：先明文连接，再用 <c>AUTH TLS</c> 升级（通常使用 21 端口）。</summary>
    Explicit,

    /// <summary>隐式 FTPS：连接建立后立即进行 TLS 握手（通常使用 990 端口，由宿主设置 <see cref="FtpClientOptions.Port"/>）。</summary>
    Implicit,
}
