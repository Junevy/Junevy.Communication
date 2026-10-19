using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>服务端证书的来源。</summary>
public enum ServerCertificateKind
{
    /// <summary>证书存储中按指纹查找的证书。</summary>
    Store,

    /// <summary>PFX 文件；口令只保存环境变量名。</summary>
    Pfx,
}

/// <summary>TCP 服务端的 TLS 参数（计划第 7.13 节）：服务器证书，以及是否要求客户端证书。映射到 <see cref="TcpServerTlsOptions"/>。</summary>
public sealed partial class ServerTlsEditor : ValidatedEditor
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private ServerCertificateKind _certificate = ServerCertificateKind.Pfx;

    [ObservableProperty]
    private string _thumbprint = string.Empty;

    [ObservableProperty]
    private string _pfxPath = string.Empty;

    [ObservableProperty]
    private string _pfxPasswordVariable = string.Empty;

    [ObservableProperty]
    private bool _clientCertificateRequired;

    [ObservableProperty]
    private bool _checkCertificateRevocation = true;

    /// <summary>生成库的 TLS 选项。未启用时证书不参与映射。</summary>
    public TcpServerTlsOptions ToOptions()
    {
        var options = new TcpServerTlsOptions
        {
            Enabled = Enabled,
            ClientCertificateRequired = ClientCertificateRequired,
            CheckCertificateRevocation = CheckCertificateRevocation,
        };
        if (!Enabled)
        {
            return options;
        }

        options.ServerCertificate = Certificate switch
        {
            ServerCertificateKind.Store => new CertificateSource { Thumbprint = Thumbprint },
            _ => new CertificateSource
            {
                PfxPath = PfxPath,
                PfxPasswordEnvironmentVariable = PfxPasswordVariable.Length == 0 ? null : PfxPasswordVariable,
            },
        };
        return options;
    }
}
