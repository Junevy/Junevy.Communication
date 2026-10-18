using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>构造指向模拟服务器的客户端选项。读超时取 2 秒，让失败用例很快结束。</summary>
public static class FtpTestClients
{
    /// <summary>指向模拟服务器的选项。</summary>
    /// <param name="server">模拟服务器。</param>
    /// <param name="userName">用户名；为 null 时匿名登录。</param>
    /// <param name="password">密码。</param>
    /// <param name="encryption">加密方式。</param>
    /// <param name="allowUntrusted">是否接受不受信任的证书。</param>
    /// <returns>新的选项对象。</returns>
    public static FtpClientOptions OptionsFor(
        FakeFtpServer server,
        string? userName = "line1",
        string? password = "secret",
        FtpEncryption encryption = FtpEncryption.None,
        bool allowUntrusted = false) => new()
    {
        Host = LoopbackTcp.Host,
        Port = server.Port,
        UserName = userName,
        Password = password,
        Encryption = encryption,
        AllowUntrustedCertificates = allowUntrusted,
        ConnectTimeout = 5000,
        ReadTimeout = 2000,
        DataConnectionTimeout = 2000,
    };
}
