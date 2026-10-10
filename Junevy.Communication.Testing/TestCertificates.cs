using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Junevy.Communication.Testing;

/// <summary>
/// 测试用自签名证书的生成器（计划 13.3）：RSA 2048，SAN 为 <c>localhost</c> 与 <c>127.0.0.1</c>，EKU 为 serverAuth + clientAuth，
/// 有效期从昨天起两年。两个目标都使用 <see cref="CertificateRequest"/> 生成（net472 自 .NET Framework 4.7.2 起提供该类型）。
/// 证书不写入任何证书存储。
/// </summary>
public static class TestCertificates
{
    /// <summary>
    /// 生成自签名测试证书（含私钥）。调用方负责 <see cref="TestCertificate.Dispose"/>。
    /// </summary>
    /// <param name="subject">证书主题，默认 <c>CN=localhost</c>。</param>
    /// <returns>测试证书。</returns>
    public static TestCertificate CreateSelfSigned(string subject = "CN=localhost")
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 selfSigned = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));

        // SChannel 不接受仅存在于内存中的临时密钥：先导出 PFX，再以 Exportable 重新导入。
        // 导入时不加 PersistKeySet，私钥不写入密钥存储，释放证书时随之删除。
        string password = Guid.NewGuid().ToString("N");
        byte[] pfx = selfSigned.Export(X509ContentType.Pfx, password);
        return new TestCertificate(new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable));
    }
}

/// <summary>
/// 测试用证书（含私钥）。<see cref="Dispose"/> 释放证书；私钥不持久化，随证书一并释放。
/// </summary>
public sealed class TestCertificate : IDisposable
{
    private bool disposed;

    internal TestCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
    }

    /// <summary>证书（含私钥）。</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>指纹（大写十六进制）。</summary>
    public string Thumbprint => Certificate.Thumbprint;

    /// <summary>释放证书。可重复调用。</summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Certificate.Dispose();
    }
}
