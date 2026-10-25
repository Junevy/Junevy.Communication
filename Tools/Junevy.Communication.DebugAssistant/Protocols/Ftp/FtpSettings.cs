using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Ftp;

namespace Junevy.Communication.DebugAssistant.Protocols.Ftp;

/// <summary>
/// FTP/FTPS 页签的参数（计划第 7.12 节、DA-D11）：服务器、端口、用户名、加密方式、证书策略、文件名编码与三个超时。
/// 密码不保存；远程目录、目录列表与传输属于一次会话。
/// </summary>
public sealed record FtpSettings(
    string Host,
    int Port,
    string? UserName,
    FtpEncryption Encryption,
    bool AllowUntrustedCertificates,
    string? EncodingName,
    int ConnectTimeout,
    int ReadTimeout,
    int DataConnectionTimeout)
    : SessionSettings;
