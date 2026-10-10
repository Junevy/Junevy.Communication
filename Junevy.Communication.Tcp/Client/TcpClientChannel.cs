using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 客户端通道（设计文档 7.1）。在 <see cref="StreamClientChannel"/> 之上实现 TCP 传输：连接超时、地址尝试、套接字选项、
/// 优雅关闭（Shutdown Send）与强制中止（销毁套接字）。TLS 在后续阶段实现，本阶段 <c>Tls.Enabled</c> 为 true 时连接返回 <c>NotSupported</c>。
/// </summary>
/// <remarks>
/// 构造时校验并复制配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，运行行为只依赖构造时的快照。
/// 名称默认为 <c>Host:Port</c>。
/// </remarks>
public sealed class TcpClientChannel : StreamClientChannel, ITcpClientChannel
{
    private const int MaxPort = 65535;

    private readonly TcpClientChannelConfig config;
    private readonly TcpConnector connector;
    private readonly bool tlsEnabled;
    private readonly string endpointText;
    private readonly object socketSync = new object();
    private Socket? activeSocket;

    /// <summary>
    /// 创建 TCP 客户端通道，名称默认为 <c>Host:Port</c>。
    /// </summary>
    /// <param name="config">TCP 客户端配置；构造时校验并复制。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、重连）；为 null 时只使用配置。</param>
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
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、重连）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">名称或配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public TcpClientChannel(string name, TcpClientChannelConfig config, ILogger<TcpClientChannel>? logger = null,
                            TcpChannelComponents? components = null)
        : base(name, BuildSettings(config), components, logger ?? NullLogger<TcpClientChannel>.Instance)
    {
        this.config = config;
        tlsEnabled = config.Tls.Enabled;
        endpointText = $"{config.Host}:{config.Port}";
        connector = new TcpConnector(config.Host, config.Port, ParseLocalAddress(config.LocalAddress), config.LocalPort,
                                     config.ConnectTimeout, config.Socket, logger ?? NullLogger<TcpClientChannel>.Instance);
    }

    /// <inheritdoc />
    public TcpClientChannelConfig Config => config;

    /// <inheritdoc />
    public IPEndPoint? RemoteEndPoint => ConnectedEndPoint(current => current.RemoteEndPoint);

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint => ConnectedEndPoint(current => current.LocalEndPoint);

    /// <inheritdoc />
    public bool IsTlsActive => false;

    /// <inheritdoc />
    protected override async Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
    {
        if (tlsEnabled)
            return CommResult<Stream>.Fail("TLS is not available in this version of TcpClientChannel.", CommErrorKind.NotSupported);

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

    /// <inheritdoc />
    protected override void AbortTransport()
    {
        Socket? current;
        lock (socketSync)
        {
            current = activeSocket;
            activeSocket = null;
        }

        current?.Dispose();
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
        ValidateSocket(config);
    }

    private static void ValidateSocket(TcpClientChannelConfig config)
    {
        TcpSocketOptions socket = config.Socket ?? throw new ArgumentException("Socket must not be null.", nameof(config));
        if (socket.ReceiveBufferSize < 0)
            throw new ArgumentOutOfRangeException("Socket.ReceiveBufferSize", socket.ReceiveBufferSize, "The receive buffer size must not be negative.");
        if (socket.SendBufferSize < 0)
            throw new ArgumentOutOfRangeException("Socket.SendBufferSize", socket.SendBufferSize, "The send buffer size must not be negative.");
        if (socket.LingerTime < -1)
            throw new ArgumentOutOfRangeException("Socket.LingerTime", socket.LingerTime, "The linger time must be -1 or a non-negative value.");

        TcpKeepAliveOptions keepAlive = socket.KeepAlive ?? throw new ArgumentException("Socket.KeepAlive must not be null.", nameof(config));
        if (!keepAlive.Enabled)
            return;

        if (keepAlive.Time <= 0)
            throw new ArgumentOutOfRangeException("Socket.KeepAlive.Time", keepAlive.Time, "The keep-alive time must be positive.");
        if (keepAlive.Interval <= 0)
            throw new ArgumentOutOfRangeException("Socket.KeepAlive.Interval", keepAlive.Interval, "The keep-alive interval must be positive.");
        if (keepAlive.RetryCount < 1)
            throw new ArgumentOutOfRangeException("Socket.KeepAlive.RetryCount", keepAlive.RetryCount, "The keep-alive retry count must be at least 1.");
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
