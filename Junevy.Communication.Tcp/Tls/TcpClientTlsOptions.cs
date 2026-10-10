using System.Security.Authentication;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 客户端 TLS 配置（设计文档 7.3）。本阶段只定义配置类型：<see cref="Enabled"/> 为 true 时，
/// 连接返回 <c>NotSupported</c>；TLS 握手在后续阶段实现。默认关闭。
/// </summary>
public sealed class TcpClientTlsOptions
{
    /// <summary>是否启用 TLS，默认 false。</summary>
    public bool Enabled { get; set; }

    /// <summary>证书校验与 SNI 使用的主机名；为 null 时使用 <c>Host</c>。</summary>
    public string? TargetHost { get; set; }

    /// <summary>协议版本；<c>None</c> 表示交给操作系统选择。</summary>
    public SslProtocols Protocols { get; set; } = SslProtocols.None;

    /// <summary>是否检查证书吊销，默认 true。</summary>
    public bool CheckCertificateRevocation { get; set; } = true;

    /// <summary>是否接受不受信任的服务端证书，仅限调试；开启后每次连接记录一条 Warning。</summary>
    public bool AllowUntrustedServerCertificate { get; set; }

    /// <summary>客户端证书（双向认证时使用）。</summary>
    public CertificateSource? ClientCertificate { get; set; }
}
