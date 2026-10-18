namespace Junevy.Communication.Ftp;

/// <summary>
/// FTP 客户端的连接选项（P63-D2、D12–D14）。全部为可以从 appsettings 绑定的简单属性。
/// 创建客户端时校验并取快照，之后修改这个对象不影响已创建的客户端。
/// </summary>
public sealed class FtpClientOptions
{
    /// <summary>服务器的主机名或地址。必填。</summary>
    public string? Host { get; set; }

    /// <summary>控制连接的端口。隐式 FTPS 通常为 990，由宿主设置。</summary>
    public int Port { get; set; } = 21;

    /// <summary>登录用户名；为 null 时匿名登录。</summary>
    public string? UserName { get; set; }

    /// <summary>登录密码。设置时必须同时设置 <see cref="UserName"/>。</summary>
    public string? Password { get; set; }

    /// <summary>加密方式（P63-D13）。</summary>
    public FtpEncryption Encryption { get; set; } = FtpEncryption.None;

    /// <summary>
    /// 为 true 时接受任何证书，包括不受信任的证书与主机名不符的证书；创建客户端时记一条 Warning（P63-D13）。
    /// </summary>
    public bool AllowUntrustedCertificates { get; set; }

    /// <summary>
    /// 文件名与列表的编码，例如 <c>GBK</c>。为 null 时，服务器支持 UTF-8 就用 UTF-8，否则用 SDK 的默认编码（P63-D12）。
    /// </summary>
    public string? EncodingName { get; set; }

    /// <summary>建立控制连接的超时时间，单位毫秒。</summary>
    public int ConnectTimeout { get; set; } = 10000;

    /// <summary>等待控制连接上应答的超时时间，单位毫秒。</summary>
    public int ReadTimeout { get; set; } = 15000;

    /// <summary>建立数据连接与读取数据的超时时间，单位毫秒。</summary>
    public int DataConnectionTimeout { get; set; } = 15000;
}
