using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using SdkFtpClient = FluentFTP.AsyncFtpClient;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// FluentFTP 客户端的包装（P6-C5）：只持有 SDK 的一个客户端，对外只暴露 BCL 与本库的类型。
/// SDK 的异常原样抛出，由 <see cref="FtpLifecycle"/> 经 <see cref="FtpErrorMap"/> 翻译。
/// </summary>
internal sealed class FtpSdkSession : IAsyncDisposable
{
    private readonly SdkFtpClient client;

    /// <summary>创建 SDK 客户端（不连接）。</summary>
    /// <param name="settings">已通过校验的连接设置。</param>
    /// <param name="loggerFactory">日志工厂；SDK 的日志经 <see cref="FtpSdkLogBridge"/> 进入它。</param>
    public FtpSdkSession(FtpConnectionSettings settings, ILoggerFactory loggerFactory)
    {
        FluentFTP.FtpConfig config = FtpSdkConfigMap.ToConfig(settings);
        var bridge = new FtpSdkLogBridge(loggerFactory);

        // SDK 不接受空的 NetworkCredential：匿名登录使用不带凭据的重载。
        client = settings.UserName is null
            ? new SdkFtpClient(settings.Host, settings.Port, config, bridge)
            : new SdkFtpClient(
                settings.Host,
                new NetworkCredential(settings.UserName, settings.Password ?? string.Empty),
                settings.Port,
                config,
                bridge);

        Encoding? encoding = settings.EncodingName is null ? null : FtpEncodings.Resolve(settings.EncodingName);
        if (encoding is not null)
        {
            client.Encoding = encoding;
        }
    }

    /// <summary>SDK 报告的控制连接状态。</summary>
    public bool IsConnected => client.IsConnected;

    /// <summary>建立控制连接并登录。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时已连接。</returns>
    public Task ConnectAsync(CancellationToken cancellationToken) => client.Connect(cancellationToken);

    /// <summary>发送 QUIT 并关闭控制连接。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>断开完成。</returns>
    public Task DisconnectAsync(CancellationToken cancellationToken) => client.Disconnect(cancellationToken);

    /// <summary>释放 SDK 客户端，关闭所有套接字。</summary>
    /// <returns>释放完成。</returns>
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
