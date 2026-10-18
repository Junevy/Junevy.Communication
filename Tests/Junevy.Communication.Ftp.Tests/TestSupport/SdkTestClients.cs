using FluentFTP;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>直接使用 FluentFTP 的测试客户端，用于钉住 SDK 的行为（第 18.2 节）。配置接近库的配置：被动数据连接、不重试、接受任何证书、读超时 2 秒。</summary>
internal static class SdkTestClients
{
    /// <summary>连接模拟服务器并返回已登录的 SDK 客户端；调用方负责释放。</summary>
    /// <param name="server">模拟服务器。</param>
    /// <returns>已连接的 SDK 客户端。</returns>
    public static async Task<AsyncFtpClient> ConnectAsync(FakeFtpServer server)
    {
        var config = new FtpConfig
        {
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            RetryAttempts = 1,
            ValidateAnyCertificate = true,
            ConnectTimeout = 5000,
            ReadTimeout = 2000,
            DataConnectionConnectTimeout = 2000,
            DataConnectionReadTimeout = 2000,
        };
        var sdk = new AsyncFtpClient(LoopbackTcp.Host, "line1", "secret", server.Port, config);
        await sdk.Connect(CancellationToken.None);
        return sdk;
    }
}
