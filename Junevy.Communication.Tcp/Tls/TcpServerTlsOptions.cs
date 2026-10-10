using System.Security.Authentication;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 服务端 TLS 配置（设计文档 7.3）。<see cref="Enabled"/> 为 true 时，服务端证书在构造 <see cref="TcpServer"/> 时加载，
/// 每个会话在 <c>SessionHandshakeTimeout</c> 内完成 TLS 认证（与初始化器共享该时限）。默认关闭。
/// </summary>
public sealed class TcpServerTlsOptions
{
    /// <summary>是否启用 TLS，默认 false。</summary>
    public bool Enabled { get; set; }

    /// <summary>服务端证书。</summary>
    public CertificateSource? ServerCertificate { get; set; }

    /// <summary>是否要求客户端证书（双向认证），默认 false。</summary>
    public bool ClientCertificateRequired { get; set; }

    /// <summary>协议版本；<c>None</c> 表示交给操作系统选择。</summary>
    public SslProtocols Protocols { get; set; } = SslProtocols.None;

    /// <summary>是否检查证书吊销，默认 true。</summary>
    public bool CheckCertificateRevocation { get; set; } = true;
}
