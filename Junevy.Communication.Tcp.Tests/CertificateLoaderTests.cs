using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 证书来源的加载与指纹清洗（计划 13.2 与审阅者要求的额外测试）。不向任何证书存储写入内容。
/// </summary>
public sealed class CertificateLoaderTests
{
    [Fact(Timeout = 30000)]
    public void Thumbprint_IsSanitized()
    {
        // 从 Windows 证书管理器复制的指纹常带 U+200E / U+202A 等不可见字符、空格、冒号与小写字母。
        string copied = "‎ 1a:2B c3‏ d4‪ e5‬";

        Assert.Equal("1A2BC3D4E5", CertificateLoader.SanitizeThumbprint(copied));
    }

    [Fact(Timeout = 30000)]
    public void CertificateSource_ThumbprintNotFound_Throws()
    {
        // 指纹在默认存储（LocalMachine\My）中不存在：抛出 ArgumentException，消息含清洗后的指纹与存储位置。
        var source = new CertificateSource
        {
            Thumbprint = "‎01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67‏",
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => CertificateLoader.Load(source, "Tls.ServerCertificate"));

        Assert.Contains("0123456789ABCDEF0123456789ABCDEF01234567", ex.Message);
        Assert.Contains("LocalMachine", ex.Message);
    }

    [Fact(Timeout = 30000)]
    public void CertificateSource_PfxWithoutPrivateKey_Throws()
    {
        // 只含证书、不含私钥的 PFX：服务端证书与客户端证书都需要私钥，因此抛出 ArgumentException。
        using TestCertificate certificate = TestCertificates.CreateSelfSigned();
        string path = TempPath();
        try
        {
            File.WriteAllBytes(path, PublicOnlyPfx(certificate.Certificate));
            var source = new CertificateSource { PfxPath = path };

            ArgumentException ex = Assert.Throws<ArgumentException>(() => CertificateLoader.Load(source, "Tls.ServerCertificate"));

            Assert.Contains("private key", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 30000)]
    public void CertificateSource_MissingEnvVar_Throws()
    {
        // 配置了密码环境变量名，但环境变量不存在：抛出 ArgumentException，消息含变量名。
        string variable = "JUNEVY_TEST_MISSING_PFX_PASSWORD_" + Guid.NewGuid().ToString("N");
        var source = new CertificateSource
        {
            PfxPath = Path.Combine(Path.GetTempPath(), "junevy-missing-" + Guid.NewGuid().ToString("N") + ".pfx"),
            PfxPasswordEnvironmentVariable = variable,
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => CertificateLoader.Load(source, "Tls.ServerCertificate"));

        Assert.Contains(variable, ex.Message);
    }

    [Fact(Timeout = 30000)]
    public void CertificateSource_PfxWithEnvironmentPassword_LoadsPrivateKey()
    {
        // 密码从环境变量读取；加载结果必须带私钥。
        using TestCertificate certificate = TestCertificates.CreateSelfSigned();
        string variable = "JUNEVY_TEST_PFX_PASSWORD_" + Guid.NewGuid().ToString("N");
        const string password = "junevy-test-password";
        string path = TempPath();
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            File.WriteAllBytes(path, certificate.Certificate.Export(X509ContentType.Pfx, password));
            Environment.SetEnvironmentVariable(variable, password);
            var source = new CertificateSource { PfxPath = path, PfxPasswordEnvironmentVariable = variable };

            using X509Certificate2 loaded = CertificateLoader.Load(source, "Tls.ServerCertificate");

            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            File.Delete(path);
        }
    }

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "junevy-test-" + Guid.NewGuid().ToString("N") + ".pfx");

    // 只含证书（无私钥）的无密码 PFX 字节（与测试中未配置密码变量的来源一致）。若当前平台无法导出仅含证书的 PFX，则以 DER 证书代替：两者都不含私钥，加载路径相同。
    private static byte[] PublicOnlyPfx(X509Certificate2 certificate)
    {
        using var publicOnly = new X509Certificate2(certificate.RawData);
        try
        {
            return publicOnly.Export(X509ContentType.Pfx);
        }
        catch (CryptographicException)
        {
            return publicOnly.RawData;
        }
    }
}
