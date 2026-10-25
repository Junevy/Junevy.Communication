using System.ComponentModel.DataAnnotations;
using System.Security.Authentication;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>TCP 套接字选项编辑器（计划第 7.13 节）。映射到 <see cref="TcpSocketOptions"/>。</summary>
public sealed partial class SocketEditor : ValidatedEditor
{
    [ObservableProperty]
    private bool _noDelay = true;

    [ObservableProperty]
    [Range(0, 16777216, ErrorMessage = "接收缓冲区必须在 0 到 16777216 之间（0 表示系统默认）。")]
    private int _receiveBufferSize;

    [ObservableProperty]
    [Range(0, 16777216, ErrorMessage = "发送缓冲区必须在 0 到 16777216 之间（0 表示系统默认）。")]
    private int _sendBufferSize;

    /// <summary>关闭时等待发送完成的秒数，-1 表示系统默认。</summary>
    [ObservableProperty]
    [Range(-1, 65535, ErrorMessage = "Linger 时间必须为 -1（默认）或 0 到 65535 秒。")]
    private int _lingerTime = -1;

    [ObservableProperty]
    private bool _keepAliveEnabled = true;

    [ObservableProperty]
    [Range(1, 86400000, ErrorMessage = "KeepAlive 首次探测时间必须在 1 到 86400000 毫秒之间。")]
    private int _keepAliveTime = 30000;

    [ObservableProperty]
    [Range(1, 86400000, ErrorMessage = "KeepAlive 探测间隔必须在 1 到 86400000 毫秒之间。")]
    private int _keepAliveInterval = 5000;

    [ObservableProperty]
    [Range(1, 100, ErrorMessage = "KeepAlive 探测次数必须在 1 到 100 之间。")]
    private int _keepAliveRetryCount = 3;

    public TcpSocketOptions ToOptions() => new()
    {
        NoDelay = NoDelay,
        ReceiveBufferSize = ReceiveBufferSize,
        SendBufferSize = SendBufferSize,
        LingerTime = LingerTime,
        KeepAlive = new TcpKeepAliveOptions
        {
            Enabled = KeepAliveEnabled,
            Time = KeepAliveTime,
            Interval = KeepAliveInterval,
            RetryCount = KeepAliveRetryCount,
        },
    };
}

/// <summary>
/// TLS 编辑器（客户端，计划第 7.13 节）。证书只填环境变量名，不保存口令（计划 DA-D11）。
/// 映射到 <see cref="TcpClientTlsOptions"/>。
/// </summary>
public sealed partial class TlsEditor : ValidatedEditor
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _targetHost = string.Empty;

    [ObservableProperty]
    private SslProtocols _protocols = SslProtocols.None;

    [ObservableProperty]
    private bool _checkCertificateRevocation = true;

    /// <summary>允许不受信任的服务器证书。开启时界面必须提示风险。</summary>
    [ObservableProperty]
    private bool _allowUntrustedServerCertificate;

    /// <summary>客户端证书的来源：不使用、证书存储或 PFX 文件。</summary>
    [ObservableProperty]
    private ClientCertificateKind _clientCertificate = ClientCertificateKind.None;

    [ObservableProperty]
    private string _thumbprint = string.Empty;

    [ObservableProperty]
    private string _pfxPath = string.Empty;

    /// <summary>保存 PFX 口令的环境变量名（不是口令本身）。</summary>
    [ObservableProperty]
    private string _pfxPasswordVariable = string.Empty;

    public TcpClientTlsOptions ToOptions()
    {
        var options = new TcpClientTlsOptions
        {
            Enabled = Enabled,
            TargetHost = TargetHost.Length == 0 ? null : TargetHost,
            Protocols = Protocols,
            CheckCertificateRevocation = CheckCertificateRevocation,
            AllowUntrustedServerCertificate = AllowUntrustedServerCertificate,
        };
        if (!Enabled)
        {
            return options;
        }

        options.ClientCertificate = ClientCertificate switch
        {
            ClientCertificateKind.Store => new CertificateSource { Thumbprint = Thumbprint },
            ClientCertificateKind.Pfx => new CertificateSource
            {
                PfxPath = PfxPath,
                PfxPasswordEnvironmentVariable = PfxPasswordVariable.Length == 0 ? null : PfxPasswordVariable,
            },
            _ => null,
        };
        return options;
    }
}

/// <summary>客户端证书的来源。</summary>
public enum ClientCertificateKind
{
    None,
    Store,
    Pfx,
}
