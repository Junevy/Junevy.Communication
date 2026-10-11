using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 会话握手（P25 Task 13，补审第 2 节）：TLS 认证（含客户端证书校验）、创建会话通道与初始化器，三者共享
/// <c>SessionHandshakeTimeout</c> 总时限；初始化器的执行与放弃语义复用 <see cref="HandshakeRunner"/>（Task 11）。
/// 时限耗尽、拆除或取消后不再等待（它们可能忽略取消令牌），但观察它们的异常。
/// </summary>
internal sealed class TcpSessionHandshake
{
    private readonly TcpServerOptions options;
    private readonly ILogger logger;
    private readonly X509Certificate2? serverCertificate;
    private readonly Func<TcpSession, Stream, bool> openChannel;
    private readonly RemoteCertificateValidationCallback clientCertificateValidation;

    /// <summary>创建握手。</summary>
    /// <param name="options">服务端的已校验选项。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="serverCertificate">服务端证书（启用 TLS 时）。</param>
    /// <param name="openChannel">在服务端锁下创建并启动会话通道的回调；拆除之后返回 false。</param>
    public TcpSessionHandshake(TcpServerOptions options, ILogger logger, X509Certificate2? serverCertificate,
                               Func<TcpSession, Stream, bool> openChannel)
    {
        this.options = options;
        this.logger = logger;
        this.serverCertificate = serverCertificate;
        this.openChannel = openChannel;
        clientCertificateValidation = ValidateClientCertificate;
    }

    /// <summary>
    /// 握手（计划 11.2、设计文档第 10 节）：TLS 认证、创建通道与初始化器共享 SessionHandshakeTimeout 总时限。
    /// 时限在握手结束后释放，之后的加入与派发不受其约束。
    /// </summary>
    /// <param name="session">要握手的会话（其套接字已接受）。</param>
    /// <returns>握手结果。</returns>
    public async Task<CommResult> RunHandshakeAsync(TcpSession session)
    {
        using TimeoutScope scope = TimeoutScope.Start(options.SessionHandshakeTimeout, CancellationToken.None, session.AbortTransport);
        var raw = new NetworkStream(session.Socket, ownsSocket: true);

        CommResult<Stream> secured = options.TlsEnabled
            ? await SecureSessionAsync(session, raw, scope).ConfigureAwait(false)
            : CommResult<Stream>.Success(raw);
        if (!secured.IsSuccess)
            return secured.ToResult();

        if (!openChannel(session, secured.Data!))
            return CommResult.Fail("The session was closed during its handshake.", CommErrorKind.ConnectionClosed);

        var handshake = new HandshakeRunner(options.Initializer, session.ClosingToken, () => HandshakeAbandoned(session),
                                            cause => logger.LogWarning(cause, "The connection initializer of session {SessionId} threw an exception.", session.Id));
        return await handshake.RunInitializerAsync(session.View!, scope, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>应用服务端的套接字选项（握手之前）；会话已在接入后被拆除时没有需要应用的套接字。</summary>
    /// <param name="session">会话。</param>
    internal void ApplySocketOptions(TcpSession session)
    {
        try
        {
            TcpSocketConfigurator.Apply(session.Socket, options.Socket, logger, session.RemoteEndPoint.ToString());
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Applying the socket options to {Remote} failed.", session.RemoteEndPoint);
        }
        catch (ObjectDisposedException)
        {
            // 会话已在接入后被拆除：没有需要应用的套接字。
        }
    }

    // 服务端 TLS 认证。时限耗尽或会话开始拆除时不再等待（认证可能忽略取消令牌），但观察它的结果：之后成功的流会因传输已中止而被会话拒绝并释放。
    private async Task<CommResult<Stream>> SecureSessionAsync(TcpSession session, Stream raw, TimeoutScope scope)
    {
        Task<CommResult<Stream>> work = AuthenticateSessionAsync(session, raw, scope.Token);
        if (!await HandshakeRunner.WaitForStepAsync(work, scope.Token, session.ClosingToken).ConfigureAwait(false))
        {
            HandshakeRunner.ObserveAbandoned(work);
            return HandshakeAbandoned(session).As<Stream>();
        }

        if (work.IsCanceled || scope.IsTimedOut)
            return HandshakeAbandoned(session).As<Stream>();

        if (work.IsFaulted)
        {
            Exception? cause = work.Exception?.InnerException ?? work.Exception;
            logger.LogWarning(cause, "The TLS authentication of session {SessionId} threw an exception.", session.Id);
            return CommResult<Stream>.Fail("The TLS authentication threw an exception.", CommErrorKind.Unspecified, null, cause);
        }

        return work.Result;
    }

    // 服务端 TLS 认证本体：成功后把流记录到会话（会话已中止时拒绝，并释放该流）。
    private async Task<CommResult<Stream>> AuthenticateSessionAsync(TcpSession session, Stream raw, CancellationToken token)
    {
        try
        {
            SslStream secured = await TlsStreamFactory.AuthenticateServerAsync(raw, serverCertificate!, options.ClientCertificateRequired,
                                                                              options.TlsProtocols, options.TlsCheckRevocation,
                                                                              clientCertificateValidation, token).ConfigureAwait(false);
            if (!session.AttachTls(secured))
            {
                secured.Dispose();
                return CommResult<Stream>.Fail("The session was closed during its TLS handshake.", CommErrorKind.ConnectionClosed);
            }

            return CommResult<Stream>.Success(secured);
        }
        catch (AuthenticationException ex)
        {
            logger.LogWarning(ex, "The TLS authentication of session {SessionId} from {Remote} failed.", session.Id, session.RemoteEndPoint);
            return CommResult<Stream>.Fail("The TLS client authentication failed.", CommErrorKind.AuthenticationFailed, null, ex);
        }
        catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
        {
            return CommResult<Stream>.Fail("The TLS handshake lost the connection.", CommErrorKind.ConnectionClosed, null, ex);
        }
    }

    // 握手步骤未正常完成：会话已在拆除则为"拆除中"，否则为超时（时限到期或步骤被取消）。
    private CommResult HandshakeAbandoned(TcpSession session)
    {
        if (session.Closing)
            return CommResult.Fail("The session was closed during its handshake.", CommErrorKind.ConnectionClosed);

        return CommResult.Fail($"The session handshake did not complete within {options.SessionHandshakeTimeout} ms.", CommErrorKind.Timeout);
    }

    // 服务端客户端证书校验（设计文档 7.3）：组件回调优先；否则要求客户端证书时必须提供且没有校验错误，不要求时未提供证书即可，提供的证书仍须没有校验错误。
    private bool ValidateClientCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (options.RemoteCertificateValidation != null)
            return options.RemoteCertificateValidation(sender, certificate, chain, errors);

        if (certificate == null)
            return !options.ClientCertificateRequired;

        return errors == SslPolicyErrors.None;
    }
}
