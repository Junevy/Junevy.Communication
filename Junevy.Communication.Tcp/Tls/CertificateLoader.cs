using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Junevy.Communication.Tcp;

/// <summary>
/// 证书加载（内部，设计文档 7.3、计划 13.2）。有指纹时从证书存储只读查找：指纹先清洗（去掉所有非十六进制字符，例如从证书管理器复制时带入的 U+200E 与空格），
/// 再忽略大小写比较，并包括无效（已过期、不受信任）的证书。否则从 PFX 文件加载，密码只能经环境变量读取。
/// 加载结果必须带私钥：服务端证书与客户端证书都需要私钥。
/// </summary>
/// <remarks>
/// PFX 使用 <c>DefaultKeySet</c> 导入：不使用 <c>MachineKeySet</c>（导入机器密钥库通常需要管理员权限），也不要求 <c>Exportable</c>。
/// </remarks>
internal static class CertificateLoader
{
    /// <summary>
    /// 校验证书来源的结构，不访问证书存储与文件：必须给出指纹或 PFX 路径；使用 PFX 且给出了密码环境变量名时，该变量必须存在。
    /// 非法时抛出 <see cref="ArgumentException"/>（D5）。
    /// </summary>
    /// <param name="source">证书来源；不能为 null。</param>
    /// <param name="propertyName">配置属性名（用于错误消息与参数名）。</param>
    public static void Validate(CertificateSource source, string propertyName)
    {
        if (source == null)
            throw new ArgumentNullException(propertyName);

        if (string.IsNullOrWhiteSpace(source.Thumbprint))
        {
            if (string.IsNullOrWhiteSpace(source.PfxPath))
                throw new ArgumentException($"{propertyName} requires a Thumbprint or a PfxPath.", propertyName);

            ReadPassword(source, propertyName);
            return;
        }

        if (SanitizeThumbprint(source.Thumbprint!).Length == 0)
            throw new ArgumentException($"{propertyName}.Thumbprint contains no hexadecimal digits.", propertyName);
    }

    /// <summary>
    /// 按来源加载证书。指纹优先，否则使用 PFX 文件。加载失败或证书没有私钥时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="source">证书来源。</param>
    /// <param name="propertyName">配置属性名（用于错误消息）。</param>
    /// <returns>证书（带私钥）；调用方负责释放。</returns>
    public static X509Certificate2 Load(CertificateSource source, string propertyName)
    {
        Validate(source, propertyName);
        X509Certificate2 certificate = string.IsNullOrWhiteSpace(source.Thumbprint)
            ? LoadFromPfx(source, propertyName)
            : FindByThumbprint(source, propertyName);

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new ArgumentException($"The certificate for {propertyName} has no private key; a server or client certificate must include its private key.",
                                        propertyName);
        }

        return certificate;
    }

    /// <summary>
    /// 复制证书来源（构造时快照，D5）：之后修改调用方的对象不影响已创建的通道。
    /// </summary>
    /// <param name="source">证书来源。</param>
    /// <returns>副本。</returns>
    public static CertificateSource Copy(CertificateSource source)
        => new CertificateSource
        {
            StoreLocation = source.StoreLocation,
            StoreName = source.StoreName,
            Thumbprint = source.Thumbprint,
            PfxPath = source.PfxPath,
            PfxPasswordEnvironmentVariable = source.PfxPasswordEnvironmentVariable,
        };

    /// <summary>
    /// 清洗指纹：去掉所有非十六进制字符（空格、冒号、U+200E 等不可见字符），并转换为大写。
    /// </summary>
    /// <param name="thumbprint">原始指纹。</param>
    /// <returns>标准形式的指纹（大写十六进制，可能为空）。</returns>
    public static string SanitizeThumbprint(string thumbprint)
    {
        var builder = new StringBuilder(thumbprint.Length);
        foreach (char c in thumbprint)
        {
            if (IsHexDigit(c))
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    // 只读打开证书存储，按清洗后的指纹查找（不要求证书有效）。未找到时抛出 ArgumentException，消息含指纹与存储位置。
    private static X509Certificate2 FindByThumbprint(CertificateSource source, string propertyName)
    {
        string thumbprint = SanitizeThumbprint(source.Thumbprint!);
        using var store = new X509Store(source.StoreName, source.StoreLocation);
        store.Open(OpenFlags.ReadOnly);

        X509Certificate2Collection all = store.Certificates;
        X509Certificate2Collection matches = all.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        X509Certificate2? found = matches.Count > 0 ? matches[0] : null;

        // 释放未选中的副本，选中的证书交给调用方。
        foreach (X509Certificate2 candidate in all)
        {
            if (!ReferenceEquals(candidate, found))
                candidate.Dispose();
        }

        for (int i = 1; i < matches.Count; i++)
            matches[i].Dispose();

        if (found == null)
        {
            throw new ArgumentException($"No certificate with thumbprint {thumbprint} was found in {source.StoreLocation}\\{source.StoreName} ({propertyName}).",
                                        propertyName);
        }

        return found;
    }

    // 从 PFX 文件加载。密码变量缺失或文件不存在、解析失败时抛出 ArgumentException。
    private static X509Certificate2 LoadFromPfx(CertificateSource source, string propertyName)
    {
        string path = source.PfxPath!;
        string? password = ReadPassword(source, propertyName);
        if (!File.Exists(path))
            throw new ArgumentException($"The PFX file '{path}' for {propertyName} does not exist.", propertyName);

        try
        {
            return new X509Certificate2(path, password, X509KeyStorageFlags.DefaultKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"The PFX file '{path}' for {propertyName} could not be loaded: {ex.Message}", propertyName, ex);
        }
    }

    // 读取 PFX 密码：未配置变量名时视为无密码；配置了变量名但变量不存在时抛出 ArgumentException。
    private static string? ReadPassword(CertificateSource source, string propertyName)
    {
        string? variable = source.PfxPasswordEnvironmentVariable;
        if (string.IsNullOrEmpty(variable))
            return null;

        string? password = Environment.GetEnvironmentVariable(variable!);
        if (password == null)
            throw new ArgumentException($"The environment variable '{variable}' that holds the PFX password for {propertyName} is not set.", propertyName);

        return password;
    }

    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
