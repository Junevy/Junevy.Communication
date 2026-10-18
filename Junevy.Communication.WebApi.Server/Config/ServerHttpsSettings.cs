using System.Security.Cryptography.X509Certificates;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// HTTPS 证书来源的快照（内部）。调用方提供的证书优先；否则使用证书文件与口令。
/// </summary>
internal sealed class ServerHttpsSettings
{
    /// <summary>没有 https 地址时的快照：不需要证书。</summary>
    public static readonly ServerHttpsSettings NotRequired = new ServerHttpsSettings(null, null, null);

    /// <summary>创建证书来源的快照。</summary>
    /// <param name="providedCertificate">调用方提供的证书（不由服务器释放）；没有时为 null。</param>
    /// <param name="certificatePath">证书文件路径；使用调用方证书时为 null。</param>
    /// <param name="certificatePassword">证书口令；没有口令或使用调用方证书时为 null。</param>
    public ServerHttpsSettings(X509Certificate2? providedCertificate, string? certificatePath, string? certificatePassword)
    {
        ProvidedCertificate = providedCertificate;
        CertificatePath = certificatePath;
        CertificatePassword = certificatePassword;
    }

    /// <summary>调用方提供的证书；没有时为 null。</summary>
    public X509Certificate2? ProvidedCertificate { get; }

    /// <summary>证书文件路径（.pfx）。</summary>
    public string? CertificatePath { get; }

    /// <summary>证书口令；没有时为 null。</summary>
    public string? CertificatePassword { get; }
}
