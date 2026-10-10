using System.Security.Cryptography.X509Certificates;

namespace Junevy.Communication.Tcp;

/// <summary>
/// 证书来源（设计文档 7.3）。有 <see cref="Thumbprint"/> 时从证书存储按指纹查找，否则从 <see cref="PfxPath"/> 加载。
/// PFX 密码只能经环境变量读取（<see cref="PfxPasswordEnvironmentVariable"/> 给出变量名），配置中不允许出现明文密码。
/// </summary>
public sealed class CertificateSource
{
    /// <summary>证书存储位置，默认 LocalMachine。</summary>
    public StoreLocation StoreLocation { get; set; } = StoreLocation.LocalMachine;

    /// <summary>证书存储名称，默认 My。</summary>
    public StoreName StoreName { get; set; } = StoreName.My;

    /// <summary>证书指纹；用于从证书存储查找。</summary>
    public string? Thumbprint { get; set; }

    /// <summary>PFX 文件路径。</summary>
    public string? PfxPath { get; set; }

    /// <summary>保存 PFX 密码的环境变量名；未设置时视为无密码。</summary>
    public string? PfxPasswordEnvironmentVariable { get; set; }
}
