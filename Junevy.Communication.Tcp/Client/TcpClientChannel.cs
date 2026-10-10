using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 客户端通道（设计文档 7.1、7.3）。在 <see cref="StreamClientChannel"/> 之上实现 TCP 传输：连接超时、地址尝试、套接字选项、
/// 可选 TLS（在握手时限内于 <see cref="SecureStreamAsync"/> 中完成）、优雅关闭（Shutdown Send）与强制中止（销毁套接字）。
/// </summary>
/// <remarks>
/// 构造时校验并复制配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，运行行为只依赖构造时的快照。
/// 名称默认为 <c>Host:Port</c>。TLS 握手与 <c>IConnectionInitializer</c> 共享 <c>HandshakeTimeout</c>。
/// 客户端证书由配置加载时，每次连接按需加载并在连接结束时释放；由 <see cref="TcpChannelComponents.ClientCertificate"/> 提供的证书由调用方持有。
/// </remarks>
public sealed class TcpClientChannel : StreamClientChannel, ITcpClientChannel
{
    private const int MaxPort = 65535;

    private readonly TcpClientChannelConfig config;
    private readonly TcpConnector connector;
    private readonly bool tlsEnabled;
    private readonly string tlsTargetHost;
    private readonly SslProtocols tlsProtocols;
    private readonly bool tlsCheckRevocation;
    private readonly bool tlsAllowUntrusted;
    private readonly CertificateSource? tlsClientSource;
    private readonly X509Certificate2? tlsClientCertificate;
    private readonly RemoteCertificateValidationCallback? tlsValidation;
    private readonly RemoteCertificateValidationCallback serverCertificateValidation;
    private readonly ILogger<TcpClientChannel> tlsLogger;
    private readonly string endpointText;
    private readonly object socketSync = new object();
    private Socket? activeSocket;
    private SslStream? activeTls;
    private volatile bool tlsActive;

    /// <summary>
    /// 创建 TCP 客户端通道，名称默认为 <c>Host:Port</c>。
    /// </summary>
    /// <param name="config">TCP 客户端配置；构造时校验并复制。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、重连、TLS 证书与校验回调）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public TcpClientChannel(TcpClientChannelConfig config, ILogger<TcpClientChannel>? logger = null, TcpChannelComponents? components = null)
        : this(DefaultName(config), config, logger, components)
    {
    }

    /// <summary>
    /// 创建具有指定名称的 TCP 客户端通道。
    /// </summary>
    /// <param name="name">通道名称（与注册表中的名称一致）。</param>
    /// <param name="config">TCP 客户端配置；构造时校验并复制。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、重连、TLS 证书与校验回调）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">名称或配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public TcpClientChannel(string name, TcpClientChannelConfig config, ILogger<TcpClientChannel>? logger = null,
                            TcpChannelComponents? components = null)
        : base(name, BuildSettings(config), components, logger ?? NullLogger<TcpClientChannel>.Instance)
    {
        this.config = config;
        endpointText = $"{config.Host}:{config.Port}";
        connector = new TcpConnector(config.Host, config.Port, ParseLocalAddress(config.LocalAddress), config.LocalPort,
                                     config.ConnectTimeout, config.Socket, logger ?? NullLogger<TcpClientChannel>.Instance);

        // TLS 配置在构造时复制（快照语义，D5）。
        TcpClientTlsOptions tls = config.Tls;
        ValidateTls(tls, components);
        tlsEnabled = tls.Enabled;
        tlsTargetHost = tls.TargetHost ?? config.Host;
        tlsProtocols = tls.Protocols;
        tlsCheckRevocation = tls.CheckCertificateRevocation;
        tlsAllowUntrusted = tls.AllowUntrustedServerCertificate;
        tlsClientSource = tlsEnabled && tls.ClientCertificate != null ? CertificateLoader.Copy(tls.ClientCertificate) : null;
        tlsClientCertificate = components?.ClientCertificate;
        tlsValidation = components?.RemoteCertificateValidation;
        serverCertificateValidation = ValidateServerCertificate;
        tlsLogger = logger ?? NullLogger<TcpClientChannel>.Instance;
    }

    /// <inheritdoc />
    public TcpClientChannelConfig Config => config;

    /// <inheritdoc />
    public IPEndPoint? RemoteEndPoint => ConnectedEndPoint(current => current.RemoteEndPoint);

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint => ConnectedEndPoint(current => current.LocalEndPoint);

    /// <inheritdoc />
    public bool IsTlsActive => tlsActive;

    /// <inheritdoc />
    protected override async Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
    {
        CommResult<Socket> connected = await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!connected.IsSuccess)
            return connected.As<Stream>();

        Socket socket = connected.Data!;
        Socket? previous;
        lock (socketSync)
        {
            previous = activeSocket;
            activeSocket = socket;
        }

        previous?.Dispose();
        return CommResult<Stream>.Success(new NetworkStream(socket, ownsSocket: true));
    }

    /// <summary>
    /// 在握手时限内完成客户端 TLS 认证（启用 TLS 时）。证书校验与错误归类见设计文档 7.3：认证失败为 <c>AuthenticationFailed</c>，
    /// 证书无法加载为 <c>InvalidRequest</c>，其他 I/O 错误为 <c>ConnectionClosed</c>；超时由基类归类为 <c>Timeout</c>。
    /// </summary>
    /// <param name="stream">已连接的网络流。</param>
    /// <param name="cancellationToken">取消令牌（握手时限内有效）。</param>
    /// <returns>认证后的流，或失败结果。</returns>
    protected override async Task<CommResult<Stream>> SecureStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (!tlsEnabled)
            return CommResult<Stream>.Success(stream);

        // 配置中的客户端证书在本次连接时加载，握手结束后释放；组件提供的证书由调用方持有，不释放。
        X509Certificate2? loaded = null;
        try
        {
            X509Certificate2? clientCertificate = tlsClientCertificate;
            if (clientCertificate == null && tlsClientSource != null)
            {
                loaded = CertificateLoader.Load(tlsClientSource, "Tls.ClientCertificate");
                clientCertificate = loaded;
            }

            SslStream secured = await TlsStreamFactory.AuthenticateClientAsync(stream, tlsTargetHost, tlsProtocols, tlsCheckRevocation,
                                                                              clientCertificate, serverCertificateValidation, cancellationToken)
                .ConfigureAwait(false);
            return AttachTls(secured);
        }
        catch (AuthenticationException ex)
        {
            tlsLogger.LogWarning(ex, "The TLS authentication with {Endpoint} failed.", endpointText);
            return CommResult<Stream>.Fail("The TLS authentication with the server failed.", CommErrorKind.AuthenticationFailed, null, ex);
        }
        catch (ArgumentException ex)
        {
            tlsLogger.LogWarning(ex, "The TLS client certificate could not be loaded.");
            return CommResult<Stream>.Fail("The TLS client certificate could not be loaded.", CommErrorKind.InvalidRequest, null, ex);
        }
        catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
        {
            tlsLogger.LogWarning(ex, "The TLS handshake with {Endpoint} lost the connection.", endpointText);
            return CommResult<Stream>.Fail("The TLS handshake lost the connection.", CommErrorKind.ConnectionClosed, null, ex);
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    /// <inheritdoc />
    protected override void AbortTransport()
    {
        Socket? socket;
        SslStream? secured;
        lock (socketSync)
        {
            socket = activeSocket;
            activeSocket = null;
            secured = activeTls;
            activeTls = null;
            tlsActive = false;
        }

        socket?.Dispose();
        secured?.Dispose();
    }

    /// <inheritdoc />
    protected override Task OnClosingAsync(CancellationToken cancellationToken)
    {
        Socket? current = CurrentSocket;
        if (current != null)
        {
            try
            {
                current.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
                // 对端可能已经断开：优雅关闭只是尽力而为，之后的中止会释放套接字。
            }
            catch (ObjectDisposedException)
            {
                // 套接字已被中止。
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override string DescribeEndpoint() => endpointText;

    private Socket? CurrentSocket
    {
        get
        {
            lock (socketSync)
                return activeSocket;
        }
    }

    // 记录认证成功的流。若传输已被中止（超时或停止），释放该流并报告失败：之后的连接不会使用它。
    private CommResult<Stream> AttachTls(SslStream secured)
    {
        lock (socketSync)
        {
            if (activeSocket != null)
            {
                activeTls = secured;
                tlsActive = true;
                return CommResult<Stream>.Success(secured);
            }
        }

        secured.Dispose();
        return CommResult<Stream>.Fail("The transport was aborted during the TLS handshake.", CommErrorKind.ConnectionClosed);
    }

    // 服务端证书校验（设计文档 7.3）：组件回调优先；否则 AllowUntrustedServerCertificate 为 true 时接受任何结果并记 Warning；否则要求没有校验错误。
    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (tlsValidation != null)
            return tlsValidation(sender, certificate, chain, errors);

        if (!tlsAllowUntrusted)
            return errors == SslPolicyErrors.None;

        tlsLogger.LogWarning("AllowUntrustedServerCertificate accepted the certificate of {Endpoint} despite validation errors {Errors} (thumbprint {Thumbprint}).",
                             endpointText, errors, TlsStreamFactory.DescribeCertificate(certificate));
        return true;
    }

    // 只有 Connected 时才返回端点；套接字已被中止或端点不可用时返回 null。
    private IPEndPoint? ConnectedEndPoint(Func<Socket, EndPoint?> select)
    {
        if (State != ConnectionState.Connected)
            return null;

        Socket? current = CurrentSocket;
        if (current == null)
            return null;

        try
        {
            return select(current) as IPEndPoint;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static string DefaultName(TcpClientChannelConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return $"{config.Host}:{config.Port}";
    }

    private static IPAddress? ParseLocalAddress(string? text)
        => text is { Length: > 0 } ? IPAddress.Parse(text) : null;

    // 校验配置并生成基类的运行参数（D5）。派生的子对象由基类在构造期间读取并复制，不保留对调用方对象的引用。
    private static ClientChannelSettings BuildSettings(TcpClientChannelConfig config)
    {
        Validate(config);
        return new ClientChannelSettings
        {
            HandshakeTimeout = config.HandshakeTimeout,
            SendTimeout = config.SendTimeout,
            RequestTimeout = config.RequestTimeout,
            LateReplyWindow = config.LateReplyWindow,
            IdleTimeout = config.IdleTimeout,
            PartialFrameTimeout = config.PartialFrameTimeout,
            DisconnectTimeout = config.DisconnectTimeout,
            Framing = config.Framing,
            Correlation = config.Correlation,
            ResetOnRequestTimeout = config.ResetOnRequestTimeout,
            Heartbeat = config.Heartbeat,
            Reconnect = config.Reconnect,
            ReceiveQueueCapacity = config.ReceiveQueueCapacity,
            QueueFullMode = config.QueueFullMode,
        };
    }

    private static void Validate(TcpClientChannelConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.Host))
            throw new ArgumentException("Host must not be empty.", nameof(config));
        if (config.Port < 1 || config.Port > MaxPort)
            throw new ArgumentOutOfRangeException(nameof(TcpClientChannelConfig.Port), config.Port, "Port must be within [1, 65535].");
        if (config.LocalPort < 0 || config.LocalPort > MaxPort)
            throw new ArgumentOutOfRangeException(nameof(TcpClientChannelConfig.LocalPort), config.LocalPort, "LocalPort must be within [0, 65535].");
        if (config.LocalAddress is { Length: > 0 } localAddress && !IPAddress.TryParse(localAddress, out _))
            throw new ArgumentException("LocalAddress must be an IP address.", nameof(config));

        RequirePositive(config.ConnectTimeout, nameof(TcpClientChannelConfig.ConnectTimeout));
        RequirePositive(config.SendTimeout, nameof(TcpClientChannelConfig.SendTimeout));
        RequirePositive(config.RequestTimeout, nameof(TcpClientChannelConfig.RequestTimeout));
        RequireNonNegative(config.HandshakeTimeout, nameof(TcpClientChannelConfig.HandshakeTimeout));
        RequireNonNegative(config.IdleTimeout, nameof(TcpClientChannelConfig.IdleTimeout));
        RequireNonNegative(config.PartialFrameTimeout, nameof(TcpClientChannelConfig.PartialFrameTimeout));
        RequireNonNegative(config.DisconnectTimeout, nameof(TcpClientChannelConfig.DisconnectTimeout));

        if (config.Framing == null)
            throw new ArgumentException("Framing must not be null.", nameof(config));
        if (config.Heartbeat == null)
            throw new ArgumentException("Heartbeat must not be null.", nameof(config));
        if (config.Reconnect == null)
            throw new ArgumentException("Reconnect must not be null.", nameof(config));
        if (config.Tls == null)
            throw new ArgumentException("Tls must not be null.", nameof(config));
        TcpSocketConfigurator.ValidateAndCopy(config.Socket, nameof(config));
    }

    // TLS 的结构校验（D5）：仅在启用时检查。证书来源必须能被加载（结构上），组件提供的证书必须带私钥。
    private static void ValidateTls(TcpClientTlsOptions tls, TcpChannelComponents? components)
    {
        if (!tls.Enabled)
            return;

        if (tls.TargetHost != null && string.IsNullOrWhiteSpace(tls.TargetHost))
            throw new ArgumentException("Tls.TargetHost must not be blank.", nameof(TcpClientChannelConfig.Tls));
        if (tls.ClientCertificate != null)
            CertificateLoader.Validate(tls.ClientCertificate, "Tls.ClientCertificate");
        if (components?.ClientCertificate is { } certificate && !certificate.HasPrivateKey)
            throw new ArgumentException("TcpChannelComponents.ClientCertificate must include its private key.", nameof(components));
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must be positive.");
    }

    private static void RequireNonNegative(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, value, "The value must not be negative.");
    }
}
