using System.Security.Cryptography.X509Certificates;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 从 .pfx 文件加载 HTTPS 证书（内部，P32-D13）。使用 <see cref="X509KeyStorageFlags.Exportable"/> 导入：Kestrel 在 Windows 上不接受仅临时存在的密钥（调研 2.9），
/// 且导入时不持久化私钥。
/// </summary>
internal static class CertificateLoader
{
    /// <summary>加载证书。</summary>
    /// <param name="path">证书文件路径。</param>
    /// <param name="password">证书口令；没有时为 null。</param>
    /// <returns>证书（调用方负责释放）。</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">文件不是有效的证书，或口令错误。</exception>
    /// <exception cref="IOException">文件无法读取。</exception>
    /// <exception cref="UnauthorizedAccessException">没有读取权限。</exception>
    public static X509Certificate2 Load(string path, string? password)
        => new X509Certificate2(path, password, X509KeyStorageFlags.Exportable);
}
