using System.Security.Cryptography.X509Certificates;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// HTTPS 证书来源的校验（内部，P32-D13）：调用方提供的证书优先；否则必须配置证书文件，口令从环境变量读取。
/// 只有配置了 https 地址时才调用本规则。证书文件的加载在启动时进行，加载失败返回 <c>InvalidRequest</c>。
/// </summary>
internal static class ServerHttpsRules
{
    private const string Section = "config.Https";

    /// <summary>
    /// 校验证书来源并生成快照。
    /// </summary>
    /// <param name="https">HTTPS 配置。</param>
    /// <param name="providedCertificate">调用方提供的证书；没有时为 null。</param>
    /// <returns>证书来源快照。</returns>
    /// <exception cref="ArgumentException">既没有提供证书也没有证书路径，或口令变量非法。</exception>
    public static ServerHttpsSettings Create(WebApiServerHttpsOptions? https, X509Certificate2? providedCertificate)
    {
        if (providedCertificate != null)
            return new ServerHttpsSettings(providedCertificate, null, null);
        if (https == null)
            throw new ArgumentException("config.Https must not be null.", Section);
        if (string.IsNullOrWhiteSpace(https.CertificatePath))
            throw new ArgumentException("config.Https.CertificatePath must name a .pfx file when an https address is configured, unless components.Certificate is set.", Section + ".CertificatePath");

        string? password = string.IsNullOrEmpty(https.CertificatePasswordVariable)
            ? null
            : ServerCredentialRules.ReadVariable(https.CertificatePasswordVariable, Section + ".CertificatePasswordVariable");
        return new ServerHttpsSettings(null, https.CertificatePath, password);
    }
}
