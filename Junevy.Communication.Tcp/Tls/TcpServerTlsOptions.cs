using System.Security.Authentication;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 服务端 TLS 配置（设计文档 7.3）。本阶段只定义配置类型：<see cref="Enabled"/> 为 true 时，
/// <see cref="TcpServer.StartAsync"/> 返回 <c>NotSupported</c>；TLS 握手在后续阶段实现。默认关闭。
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
