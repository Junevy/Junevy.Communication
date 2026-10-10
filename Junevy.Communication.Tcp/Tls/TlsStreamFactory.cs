using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TLS 流的认证（内部，设计文档 7.3、计划 13.2）。封装 net8.0 与 net472 的 <see cref="SslStream"/> 差异：
/// net8.0 使用选项对象并接受取消令牌；net472 的 <c>AuthenticateAs*Async</c> 不接受取消令牌，超时由调用方的计时窗口销毁套接字来打断。
/// 失败时抛出异常（<see cref="AuthenticationException"/>、I/O 异常等），由调用方归类。
/// </summary>
internal static class TlsStreamFactory
{
    /// <summary>
    /// 在 <paramref name="stream"/> 上完成客户端 TLS 认证。成功返回 <see cref="SslStream"/>（不拥有 <paramref name="stream"/>，由传输统一销毁）；
    /// 失败时释放 <see cref="SslStream"/> 并重新抛出异常。
    /// </summary>
    /// <param name="stream">底层流（套接字的 NetworkStream）。</param>
    /// <param name="targetHost">校验与 SNI 使用的主机名。</param>
    /// <param name="protocols">协议版本；<see cref="SslProtocols.None"/> 表示交给操作系统。</param>
    /// <param name="checkRevocation">是否检查证书吊销。</param>
    /// <param name="clientCertificate">客户端证书；为 null 时不提供。</param>
    /// <param name="validation">服务端证书校验回调。</param>
    /// <param name="cancellationToken">取消令牌（net472 忽略，见类型说明）。</param>
    /// <returns>已认证的流。</returns>
    public static async Task<SslStream> AuthenticateClientAsync(Stream stream, string targetHost, SslProtocols protocols, bool checkRevocation,
                                                                X509Certificate2? clientCertificate, RemoteCertificateValidationCallback validation,
                                                                CancellationToken cancellationToken)
    {
        // leaveInnerStream: true——底层套接字由传输的中止统一销毁，释放 SslStream 不得影响它。
        var ssl = new SslStream(stream, leaveInnerStreamOpen: true, validation);
        try
        {
            var certificates = new X509CertificateCollection();
            if (clientCertificate != null)
                certificates.Add(clientCertificate);

#if NET8_0_OR_GREATER
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ClientCertificates = certificates,
                EnabledSslProtocols = protocols,
                CertificateRevocationCheckMode = checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = validation,
            };
            await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
#else
            // net472：AuthenticateAsClientAsync 不接受取消令牌。超时或取消时计时窗口销毁套接字，握手以异常结束，调用方不再等待它。
            await ssl.AuthenticateAsClientAsync(targetHost, certificates, protocols, checkRevocation).ConfigureAwait(false);
#endif
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在 <paramref name="stream"/> 上完成服务端 TLS 认证。语义与 <see cref="AuthenticateClientAsync"/> 相同。
    /// </summary>
    /// <param name="stream">底层流（套接字的 NetworkStream）。</param>
    /// <param name="serverCertificate">服务端证书（带私钥）。</param>
    /// <param name="clientCertificateRequired">是否要求客户端证书（双向认证）。</param>
    /// <param name="protocols">协议版本；<see cref="SslProtocols.None"/> 表示交给操作系统。</param>
    /// <param name="checkRevocation">是否检查证书吊销。</param>
    /// <param name="validation">客户端证书校验回调。</param>
    /// <param name="cancellationToken">取消令牌（net472 忽略，见类型说明）。</param>
    /// <returns>已认证的流。</returns>
    public static async Task<SslStream> AuthenticateServerAsync(Stream stream, X509Certificate2 serverCertificate, bool clientCertificateRequired,
                                                                SslProtocols protocols, bool checkRevocation, RemoteCertificateValidationCallback validation,
                                                                CancellationToken cancellationToken)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: true, validation);
        try
        {
#if NET8_0_OR_GREATER
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = clientCertificateRequired,
                EnabledSslProtocols = protocols,
                CertificateRevocationCheckMode = checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = validation,
            };
            await ssl.AuthenticateAsServerAsync(options, cancellationToken).ConfigureAwait(false);
#else
            // net472：AuthenticateAsServerAsync 不接受取消令牌，由计时窗口销毁套接字来打断。
            await ssl.AuthenticateAsServerAsync(serverCertificate, clientCertificateRequired, protocols, checkRevocation).ConfigureAwait(false);
#endif
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 用于日志的证书描述：指纹（大写十六进制），没有证书时为 <c>none</c>。
    /// </summary>
    /// <param name="certificate">证书，可为 null。</param>
    /// <returns>描述文本。</returns>
    public static string DescribeCertificate(X509Certificate? certificate)
        => certificate == null ? "none" : certificate.GetCertHashString();
}
