namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// HTTPS 的证书配置。只在配置了 https 地址时使用；若 <see cref="WebApiServerComponents.Certificate"/> 已设置，则以它为准。
/// </summary>
public sealed class WebApiServerHttpsOptions
{
    /// <summary>证书文件的路径（.pfx）。</summary>
    public string? CertificatePath { get; set; }

    /// <summary>保存证书口令的环境变量名；证书没有口令时为 null。</summary>
    public string? CertificatePasswordVariable { get; set; }
}
