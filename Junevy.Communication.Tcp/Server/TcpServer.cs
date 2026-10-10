using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.Tcp.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 服务端（设计文档 7.2、11.2）。监听指定地址与端口，为每个接入的连接创建一个 <see cref="ITcpSession"/>。
/// 会话内部是一个 <c>StreamChannel</c>，与客户端共用分帧、关联、派发与心跳代码。
/// </summary>
/// <remarks>
/// 每个会话的接入顺序：应用套接字选项 → 在 <c>SessionHandshakeTimeout</c> 总时限内完成握手（启用 TLS 时先做 TLS 认证，然后创建关联表与路由、
/// <c>BeginHandshake</c>、启动会话通道，最后执行初始化器）→ 加入会话表并排队 <c>SessionConnected</c> → 等待该事件派发完成 → <c>EndHandshake</c>（释放握手积压）→ 启动会话心跳。
/// 握手失败或超时只关闭会话，不触发任何会话事件。会话的故障（对端关闭、读写异常、心跳失败、空闲超时、协议违规）在后台拆除会话，同一会话只拆除一次。
/// 服务端事件（<c>StateChanged</c>、<c>SessionConnected</c>、<c>SessionClosed</c>）经单个派发循环按顺序、在锁外触发；
/// 会话的 <c>FrameReceived</c> 在路由的派发循环中触发，先触发会话级，再触发服务端级。
/// 在事件处理器内调用停止或关闭只发出信号、不等待，因此不会死锁（计划 D16）。
/// 配置在构造时校验并复制（D5）。启用 TLS 时服务端证书在构造时加载（找不到或没有私钥则抛出 <see cref="ArgumentException"/>），并在 <see cref="DisposeAsync"/> 中释放（组件提供的证书除外）。
/// </remarks>
public sealed class TcpServer : ITcpServer
{
    // 填充循环每次申请的缓冲大小（字节）。服务端配置未提供该项，与客户端的默认值一致。
    private const int ReceiveBufferSize = 4096;

    private readonly TcpServerConfig config;
    private readonly TcpServerOptions options;
    private readonly string name;
    private readonly ILogger logger;
    private readonly object sync = new object();
    private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
    private readonly Dictionary<long, TcpSession> sessions = new Dictionary<long, TcpSession>();
    private readonly HashSet<TcpSession> live = new HashSet<TcpSession>();
    private readonly Queue<QueuedEvent> eventQueue = new Queue<QueuedEvent>();
    private readonly AsyncLocal<DispatchScope?> dispatchScope = new AsyncLocal<DispatchScope?>();
    private readonly X509Certificate2? serverCertificate;
    private readonly bool ownsServerCertificate;
    private readonly RemoteCertificateValidationCallback clientCertificateValidation;

    // 以下字段由 sync 保护；state 例外，声明为 volatile 以便无锁读取。
    private volatile ServerState state = ServerState.Stopped;
    private Socket? listener;
    private IPEndPoint? localEndPoint;
    private Task? acceptLoop;
    private CancellationTokenSource? restartSource;
    private Task? stopTask;
    private bool stopRequested;
    private bool dispatching;
    private int activeSessions;
    private long lastSessionId;
    private int disposed;

    /// <summary>
    /// 创建 TCP 服务端（尚未监听）。构造时校验并复制配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，调用方的配置对象不会被修改。
    /// 名称默认为 <c>ListenAddress:Port</c>。
    /// </summary>
    /// <param name="config">服务端配置。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、连接过滤器）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public TcpServer(TcpServerConfig config, ILogger<TcpServer>? logger = null, TcpChannelComponents? components = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        options = new TcpServerOptions(config, components);
        name = $"{config.ListenAddress}:{config.Port}";
        this.logger = logger ?? NullLogger<TcpServer>.Instance;
        clientCertificateValidation = ValidateClientCertificate;

        if (options.TlsEnabled)
        {
            ownsServerCertificate = options.ComponentServerCertificate == null;
            serverCertificate = options.ComponentServerCertificate
                ?? CertificateLoader.Load(options.ServerCertificateSource!, "Tls.ServerCertificate");
        }
    }

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public TcpServerConfig Config => config;

    /// <inheritdoc />
    public ServerState State => state;

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint
    {
        get
        {
            lock (sync)
                return localEndPoint;
        }
    }

    /// <inheritdoc />
    public int SessionCount
    {
        get
        {
            lock (sync)
                return sessions.Count;
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ITcpSession> Sessions
    {
        get
        {
            lock (sync)
                return sessions.Values.Cast<ITcpSession>().ToList();
        }
    }

    /// <inheritdoc />
    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<TcpSessionEventArgs>? SessionConnected;

    /// <inheritdoc />
    public event EventHandler<TcpSessionClosedEventArgs>? SessionClosed;

    /// <inheritdoc />
    public event EventHandler<TcpSessionFrameEventArgs>? FrameReceived;

    /// <inheritdoc />
    public async Task<CommResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await WaitForPendingStopAsync().ConfigureAwait(false);

            lock (sync)
            {
                if (state != ServerState.Stopped)
                    return CommResult.Fail("The server is already started.", CommErrorKind.InvalidRequest);

                stopRequested = false;
                stopTask = null;
                SetStateLocked(ServerState.Starting, null);
            }

            CommResult<Socket> bound = Bind();
            if (!bound.IsSuccess)
            {
                lock (sync)
                {
                    if (!stopRequested)
                        SetStateLocked(ServerState.Stopped, bound.Exception);
                }

                logger.LogWarning("Starting TCP server {Name} failed: {Result}", name, bound);
                return bound.ToResult();
            }

            lock (sync)
            {
                if (stopRequested)
                {
                    bound.Data!.Dispose();
                    return CommResult.Fail("The server was stopped while it was starting.", CommErrorKind.ConnectionClosed);
                }

                StartListening(bound.Data!);
            }

            logger.LogInformation("TCP server {Name} is listening.", name);
            return CommResult.Success();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stop = BeginStop();
        if (IsInDispatchContext())
            return Task.CompletedTask;

        return WaitUnlessCancelledAsync(stop, cancellationToken);
    }

    /// <inheritdoc />
    public bool TryGetSession(long sessionId, out ITcpSession? session)
    {
        lock (sync)
        {
            if (sessions.TryGetValue(sessionId, out TcpSession? found))
            {
                session = found;
                return true;
            }
        }

        session = null;
        return false;
    }

    /// <inheritdoc />
    public Task<CommResult> SendAsync(long sessionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TcpSession? session;
        lock (sync)
            sessions.TryGetValue(sessionId, out session);

        if (session == null)
            return Task.FromResult(CommResult.Fail($"No connected session with id {sessionId}.", CommErrorKind.NotConnected));

        return session.SendAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> BroadcastAsync(ReadOnlyMemory<byte> payload, Func<ITcpSession, bool>? filter = null,
                                          CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        List<TcpSession> targets;
        lock (sync)
            targets = sessions.Values.ToList();

        var sends = new List<Task<CommResult>>(targets.Count);
        foreach (TcpSession session in targets)
        {
            if (!session.IsConnected || !FilterAccepts(filter, session))
                continue;

            sends.Add(session.SendAsync(payload, cancellationToken));
        }

        CommResult[] results = await Task.WhenAll(sends).ConfigureAwait(false);
        return results.Count(result => result.IsSuccess);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task disposing = DisposeAsync().AsTask();
        int limit = options.StopTimeout + 1000;
        if (!disposing.Wait(limit))
            logger.LogWarning("Disposing TCP server {Name} did not finish within {Timeout} ms; the stop continues in the background.", name, limit);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        Task stop = BeginStop();
        if (IsInDispatchContext())
        {
            // 派发上下文内不等待停止：证书在停止完成后释放（停止本身不会因此等待）。
            _ = stop.ContinueWith(_ => ReleaseServerCertificate(), TaskScheduler.Default);
            return default;
        }

        return new ValueTask(ReleaseAfterStopAsync(stop));
    }

    // 停止完成后释放由服务端加载的证书（组件证书由调用方释放）。
    private async Task ReleaseAfterStopAsync(Task stop)
    {
        await stop.ConfigureAwait(false);
        ReleaseServerCertificate();
    }

    private void ReleaseServerCertificate()
    {
        if (ownsServerCertificate)
            serverCertificate?.Dispose();
    }

    // ————— 监听器 —————

    // 绑定监听套接字。端口占用映射为 ResourceExhausted，其他套接字错误映射为 ConnectionClosed（计划 11.2）。
    private CommResult<Socket> Bind()
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(options.ListenAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ConfigureListener(socket);
            socket.Bind(new IPEndPoint(options.ListenAddress, options.Port));
            socket.Listen(options.Backlog);

            Socket bound = socket;
            socket = null;
            return CommResult<Socket>.Success(bound);
        }
        catch (SocketException ex)
        {
            CommErrorKind kind = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? CommErrorKind.ResourceExhausted
                : CommErrorKind.ConnectionClosed;
            return CommResult<Socket>.Fail($"Listening on {name} failed: {ex.SocketErrorCode}.", kind, null, ex);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    // Windows 上使用独占地址（端口被占用时绑定失败，而不是与其他套接字共享）；其他平台允许地址复用，便于重启后立即重新绑定。
    private void ConfigureListener(Socket socket)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            socket.ExclusiveAddressUse = true;
        else
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (options.Socket.ReceiveBufferSize > 0)
            socket.ReceiveBufferSize = options.Socket.ReceiveBufferSize;
        if (options.Socket.SendBufferSize > 0)
            socket.SendBufferSize = options.Socket.SendBufferSize;
    }

    // 调用方持有 sync。把绑定好的监听器交给接受循环，状态进入 Running。
    private void StartListening(Socket socket)
    {
        listener = socket;
        localEndPoint = socket.LocalEndPoint as IPEndPoint;
        SetStateLocked(ServerState.Running, null);
        acceptLoop = LifecycleSupport.StartDetached(() => AcceptLoopAsync(socket));
    }

    private async Task AcceptLoopAsync(Socket socket)
    {
        while (true)
        {
            Socket accepted;
            try
            {
                accepted = await AcceptSocketAsync(socket).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnListenerFailed(socket, ex);
                return;
            }

            try
            {
                AdmitSocket(accepted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admitting a connection to TCP server {Name} failed.", name);
                accepted.Dispose();
            }
        }
    }

#if NET8_0_OR_GREATER
    private static async Task<Socket> AcceptSocketAsync(Socket listener)
        => await listener.AcceptAsync().ConfigureAwait(false);
#else
    // net472 的 Socket 没有 AcceptAsync：以 BeginAccept/EndAccept 包装。停止时关闭监听器即可打断挂起的接受。
    private static Task<Socket> AcceptSocketAsync(Socket listener)
        => Task.Factory.FromAsync(listener.BeginAccept(null, null), listener.EndAccept);
#endif

    // 接受循环出现非停止原因的异常：状态进入 Faulted；启用 RestartOnFault 时在后台重新监听。停止过程中的异常不算故障。
    private void OnListenerFailed(Socket socket, Exception exception)
    {
        bool faulted;
        lock (sync)
        {
            faulted = !stopRequested && ReferenceEquals(listener, socket);
            if (faulted)
            {
                listener = null;
                localEndPoint = null;
                acceptLoop = null;
                SetStateLocked(ServerState.Faulted, exception);

                if (options.RestartPolicy != null)
                {
                    restartSource = new CancellationTokenSource();
                    CancellationToken token = restartSource.Token;
                    IBackoffPolicy policy = options.RestartPolicy;
                    LifecycleSupport.StartDetached(() => RestartListenerAsync(policy, token));
                }
            }
        }

        socket.Dispose();
        if (faulted)
            logger.LogWarning(exception, "The listener of TCP server {Name} failed; the server is faulted.", name);
    }

    // 按退避策略重新绑定同一地址。成功回到 Running；耗尽或被停止取消时保持 Faulted / 退出。
    private async Task RestartListenerAsync(IBackoffPolicy policy, CancellationToken token)
    {
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                int? delay = policy.GetDelay(attempt);
                if (delay == null)
                {
                    logger.LogError("TCP server {Name} gave up re-listening after {Attempts} attempt(s); it stays faulted.", name, attempt - 1);
                    return;
                }

                await Task.Delay(delay.Value, token).ConfigureAwait(false);

                CommResult<Socket> bound = Bind();
                if (!bound.IsSuccess)
                {
                    logger.LogWarning("Re-listening on {Name} failed (attempt {Attempt}): {Result}", name, attempt, bound);
                    continue;
                }

                lock (sync)
                {
                    if (stopRequested)
                    {
                        bound.Data!.Dispose();
                        return;
                    }

                    restartSource = null;
                    StartListening(bound.Data!);
                }

                logger.LogInformation("TCP server {Name} re-listening after a fault.", name);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 停止或释放取消了重新监听。
        }
    }

    // 测试钩子：销毁当前监听器，使接受循环以故障路径退出。
    internal void SimulateListenerFault()
    {
        Socket? current;
        lock (sync)
            current = listener;

        current?.Dispose();
    }

    // ————— 接入与会话建立 —————

    // 接受循环中的接入检查（计划 11.2）：停止中 → MaxSessions → 白名单 → IConnectionFilter。不通过则立即关闭并记 Warning。
    private void AdmitSocket(Socket accepted)
    {
        if (!TryGetEndPoints(accepted, out IPEndPoint? remote, out IPEndPoint? local) || remote == null || local == null)
        {
            accepted.Dispose();
            return;
        }

        string? rejection = null;
        bool reserved = false;
        long sessionId = 0;
        lock (sync)
        {
            if (stopRequested)
            {
                rejection = "The server is stopping.";
            }
            else if (options.MaxSessions > 0 && activeSessions >= options.MaxSessions)
            {
                rejection = $"The session limit of {options.MaxSessions} has been reached.";
            }
            else
            {
                activeSessions++;
                reserved = true;
                sessionId = ++lastSessionId;
            }
        }

        if (rejection == null && !IsAllowedRemote(remote))
            rejection = "The remote address is not in AllowedRemoteAddresses.";
        if (rejection == null && !FilterAccepts(remote))
            rejection = "The connection filter rejected the remote address.";

        if (rejection != null)
        {
            if (reserved)
            {
                lock (sync)
                    activeSessions--;
            }

            accepted.Dispose();
            logger.LogWarning("Rejected the connection from {Remote}: {Reason}", remote, rejection);
            return;
        }

        var session = new TcpSession(this, sessionId, accepted, remote, local);
        bool admitted;
        lock (sync)
        {
            admitted = !stopRequested;
            if (admitted)
                live.Add(session);
            else
                activeSessions--;
        }

        if (!admitted)
        {
            session.AbortTransport();
            return;
        }

        LifecycleSupport.StartDetached(() => RunSessionAsync(session));
    }

    // 会话的完整生命周期：建立失败（握手失败或超时、未加入）→ 静默拆除；异常 → 拆除（已加入则派发 SessionClosed）。
    private async Task RunSessionAsync(TcpSession session)
    {
        try
        {
            bool joined = await SetupSessionAsync(session).ConfigureAwait(false);
            if (!joined)
                await TeardownAsync(session, DisconnectReason.Error, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setting up session {SessionId} from {Remote} failed.", session.Id, session.RemoteEndPoint);
            await TeardownAsync(session, DisconnectReason.Error, ex).ConfigureAwait(false);
        }
    }

    // 返回 true 表示会话已加入会话表（之后的拆除由故障或停止发起）；false 表示未加入，调用方负责静默拆除。
    private async Task<bool> SetupSessionAsync(TcpSession session)
    {
        ApplySocketOptions(session);
        CommResult handshake = await RunHandshakeAsync(session).ConfigureAwait(false);
        if (!handshake.IsSuccess)
        {
            logger.LogWarning("Session {SessionId} from {Remote} did not complete its handshake: {Result}", session.Id, session.RemoteEndPoint, handshake);
            return false;
        }

        if (!Join(session))
            return false;

        // 先派发 SessionConnected，再释放握手积压：会话的 FrameReceived 不会早于 SessionConnected 的处理器。
        await WaitForDeliveryAsync(session).ConfigureAwait(false);
        if (session.Closing)
            return true;

        try
        {
            await session.Router!.EndHandshakeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 路由已被停止：会话正在拆除，拆除流程负责派发 SessionClosed。
            return true;
        }

        StartHeartbeat(session);
        return true;
    }

    private void ApplySocketOptions(TcpSession session)
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

    // 创建关联表与路由，BeginHandshake，启动通道（计划 11.2 的顺序）。全部在服务端锁下完成，以便与拆除互斥：拆除之后不会再创建通道。
    private bool OpenChannel(TcpSession session, Stream stream)
    {
        lock (sync)
        {
            if (session.Closing)
                return false;

            var table = new PendingRequestTable(options.Correlation, options.KeyExtractor, options.LateReplyWindow, logger);
            table.BeginHandshake();

            var router = new FrameRouter(table, options.ReceiveQueueCapacity, QueueFullMode.Wait,
                args =>
                {
                    RaiseSessionFrame(session, args);
                    return Task.CompletedTask;
                }, session.Statistics, logger);

            var settings = new StreamChannelSettings
            {
                SendTimeout = options.SendTimeout,
                RequestTimeout = options.RequestTimeout,
                LateReplyWindow = options.LateReplyWindow,
                PartialFrameAction = PartialFrameAction.Disconnect,
                PartialFrameTimeout = options.PartialFrameTimeout,
                ResetOnRequestTimeout = options.ResetOnRequestTimeout,
                Correlation = options.Correlation,
                ReceiveBufferSize = ReceiveBufferSize,
            };

            var channel = new StreamChannel(stream, settings, options.Codec.CreateDecoder(), options.Codec.CreateEncoder(), table, router,
                session.Statistics, logger, (reason, exception) => ScheduleTeardown(session, reason, exception), session.AbortTransport);

            session.Attach(router, channel);
            channel.Start();
            return true;
        }
    }

    // 握手（计划 11.2、设计文档第 10 节）：TLS 认证、创建通道与初始化器共享 SessionHandshakeTimeout 总时限。
    // 时限在握手结束后释放，之后的加入与派发不受其约束。超时或拆除后不再等待（它们可能忽略取消令牌），但观察它们的异常。
    private async Task<CommResult> RunHandshakeAsync(TcpSession session)
    {
        using TimeoutScope scope = TimeoutScope.Start(options.SessionHandshakeTimeout, CancellationToken.None, session.AbortTransport);
        var raw = new NetworkStream(session.Socket, ownsSocket: true);

        CommResult<Stream> secured = options.TlsEnabled
            ? await SecureSessionAsync(session, raw, scope).ConfigureAwait(false)
            : CommResult<Stream>.Success(raw);
        if (!secured.IsSuccess)
            return secured.ToResult();

        if (!OpenChannel(session, secured.Data!))
            return CommResult.Fail("The session was closed during its handshake.", CommErrorKind.ConnectionClosed);

        if (options.Initializer == null)
            return CommResult.Success();

        return await RunInitializerAsync(session, scope).ConfigureAwait(false);
    }

    // 服务端 TLS 认证。时限耗尽或会话开始拆除时不再等待（认证可能忽略取消令牌），但观察它的结果：之后成功的流会因传输已中止而被会话拒绝并释放。
    private async Task<CommResult<Stream>> SecureSessionAsync(TcpSession session, Stream raw, TimeoutScope scope)
    {
        Task<CommResult<Stream>> work = AuthenticateSessionAsync(session, raw, scope.Token);
        if (!await WaitForHandshakeStepAsync(work, session, scope.Token).ConfigureAwait(false))
        {
            ObserveAbandoned(work);
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

    // 在 SessionHandshakeTimeout 内执行初始化器（与 TLS 共享时限）。超时或拆除后不再等待初始化器，但观察它的异常。
    private async Task<CommResult> RunInitializerAsync(TcpSession session, TimeoutScope scope)
    {
        Task<CommResult> work = StartInitializer(session.View!, scope.Token);
        if (!await WaitForHandshakeStepAsync(work, session, scope.Token).ConfigureAwait(false))
        {
            ObserveAbandoned(work);
            return HandshakeAbandoned(session);
        }

        if (work.IsCanceled || scope.IsTimedOut)
            return HandshakeAbandoned(session);

        if (work.IsFaulted)
        {
            Exception? cause = work.Exception?.InnerException ?? work.Exception;
            logger.LogWarning(cause, "The connection initializer of session {SessionId} threw an exception.", session.Id);
            return CommResult.Fail("The connection initializer threw an exception.", CommErrorKind.Unspecified, null, cause);
        }

        return work.Result;
    }

    // 等待一个握手步骤完成，或握手时限耗尽、会话开始拆除。返回 true 表示步骤已完成。
    private static async Task<bool> WaitForHandshakeStepAsync(Task work, TcpSession session, CancellationToken windowToken)
    {
        var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (windowToken.Register(() => interrupted.TrySetResult(true)))
        using (session.ClosingToken.Register(() => interrupted.TrySetResult(true)))
        {
            Task finished = await Task.WhenAny(work, interrupted.Task).ConfigureAwait(false);
            return ReferenceEquals(finished, work);
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

    // 同步抛出的异常转换为已完成的故障任务，统一由 RunInitializerAsync 处理。
    private Task<CommResult> StartInitializer(IByteChannel view, CancellationToken token)
    {
        try
        {
            return options.Initializer!.InitializeAsync(view, token);
        }
        catch (Exception ex)
        {
            return Task.FromException<CommResult>(ex);
        }
    }

    // 加入会话表并排队 SessionConnected（同一临界区，保证拆除只能在加入之后排队 SessionClosed）。
    private bool Join(TcpSession session)
    {
        lock (sync)
        {
            if (stopRequested || session.Closing)
                return false;

            session.MarkJoined(DateTimeOffset.UtcNow);
            sessions.Add(session.Id, session);

            var args = new TcpSessionEventArgs(session);
            EnqueueLocked(() => LifecycleSupport.InvokeEach(SessionConnected, this, args, logger, "SessionConnected"), session.ConnectedDelivered);
        }

        logger.LogInformation("Session {SessionId} from {Remote} connected.", session.Id, session.RemoteEndPoint);
        return true;
    }

    private static async Task WaitForDeliveryAsync(TcpSession session)
    {
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (session.ClosingToken.Register(() => closed.TrySetResult(true)))
        {
            await Task.WhenAny(session.ConnectedDelivered.Task, closed.Task).ConfigureAwait(false);
        }
    }

    // 心跳与空闲监视在握手结束之后启动（计划 9.3 的同一顺序）。超时由 HeartbeatMonitor 报告，拆除在后台进行。
    // 探测只在启用心跳时创建；用户工厂在服务端锁之外调用，工厂抛出的异常使该会话以 Error 拆除（见 RunSessionAsync）。
    private void StartHeartbeat(TcpSession session)
    {
        bool probing = options.Heartbeat.Enabled;
        if (!probing && options.SessionIdleTimeout <= 0)
            return;

        IHealthProbe? probe = probing ? CreateSessionProbe(session) : null;
        lock (sync)
        {
            if (session.Closing)
                return;

            var monitor = new HeartbeatMonitor(probe, options.Heartbeat, options.SessionIdleTimeout, session.Statistics,
                reason => ScheduleTeardown(session, reason, null), logger);

            session.Heartbeat = monitor;
            monitor.Start();
        }
    }

    // 会话自己的心跳探测：SessionHealthProbeFactory 返回的专用探测；未设置工厂时使用绑定该会话通道的内置负载探测。
    private IHealthProbe CreateSessionProbe(TcpSession session)
    {
        Func<ITcpSession, IHealthProbe>? factory = options.SessionHealthProbeFactory;
        if (factory == null)
            return new PayloadHeartbeatProbe(session.View!, options.HeartbeatPayload!, options.HeartbeatExpectedReply, options.Heartbeat.Timeout);

        IHealthProbe? probe = factory(session);
        if (probe == null)
            throw new InvalidOperationException("SessionHealthProbeFactory returned null.");

        return probe;
    }

    // ————— 拆除 —————

    // 故障与停止的入口（可从任意线程、包括解析循环与心跳计时器调用）：只调度后台拆除，不等待，同一会话的重复调度无害。
    private void ScheduleTeardown(TcpSession session, DisconnectReason reason, Exception? exception)
    {
        if (session.Closing)
            return;

        LifecycleSupport.StartDetached(() => TeardownAsync(session, reason, exception));
    }

    // 拆除会话（每个会话只执行一次）：从会话表移除 → 停止心跳 → 停止通道（中止传输、按 StopTimeout 排空）→ 已加入则排队 SessionClosed → 标记完成。
    private async Task TeardownAsync(TcpSession session, DisconnectReason reason, Exception? exception)
    {
        HeartbeatMonitor? monitor;
        StreamChannel? channel;
        bool joined;
        lock (sync)
        {
            if (session.Closing)
                return;

            session.BeginClosing();
            activeSessions--;
            joined = session.Joined;
            if (joined)
                sessions.Remove(session.Id);

            monitor = session.Heartbeat;
            session.Heartbeat = null;
            channel = session.Channel;
        }

        try
        {
            session.CancelClosingToken();

            if (monitor != null)
                await monitor.StopAsync().ConfigureAwait(false);

            session.ShutdownSend();

            if (channel != null)
                await channel.StopAsync(options.StopTimeout).ConfigureAwait(false);

            session.AbortTransport();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tearing down session {SessionId} failed; the transport is aborted.", session.Id);
            session.AbortTransport();
        }
        finally
        {
            lock (sync)
            {
                live.Remove(session);
                if (joined)
                {
                    var args = new TcpSessionClosedEventArgs(session, reason, exception);
                    EnqueueLocked(() => LifecycleSupport.InvokeEach(SessionClosed, this, args, logger, "SessionClosed"));
                }
            }

            logger.LogInformation("Session {SessionId} from {Remote} closed: {Reason}.", session.Id, session.RemoteEndPoint, reason);
            session.CompleteClosing();
        }
    }

    // 显式关闭会话（ITcpSession.CloseAsync）。在派发上下文内只发出信号；否则等待拆除完成（可取消）。
    internal Task CloseSessionAsync(TcpSession session, CancellationToken cancellationToken)
    {
        ScheduleTeardown(session, DisconnectReason.UserRequested, null);
        if (IsInDispatchContext())
            return Task.CompletedTask;

        return WaitUnlessCancelledAsync(session.Completion, cancellationToken);
    }

    // ————— 启动与停止 —————

    // 停止（计划 11.2）：Stopping → 关闭监听器 → 并行拆除全部会话（UserRequested）→ 等待接受循环与会话拆除完成（至多 StopTimeout）→ Stopped。
    // 在后台执行，由调用方等待；派发上下文内的调用方不等待。只有第一次调用启动停止，之后的调用等待同一次停止。
    private Task BeginStop()
    {
        lock (sync)
        {
            if (stopTask != null)
                return stopTask;

            if (state == ServerState.Stopped)
                return Task.CompletedTask;

            stopRequested = true;
            stopTask = LifecycleSupport.StartDetached(StopCoreAsync);
            return stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            Socket? closingListener;
            Task? accepting;
            CancellationTokenSource? restart;
            List<TcpSession> pending;
            lock (sync)
            {
                SetStateLocked(ServerState.Stopping, null);
                closingListener = listener;
                listener = null;
                localEndPoint = null;
                accepting = acceptLoop;
                acceptLoop = null;
                restart = restartSource;
                restartSource = null;
                pending = live.ToList();
            }

            restart?.Cancel();
            closingListener?.Dispose();

            foreach (TcpSession session in pending)
                ScheduleTeardown(session, DisconnectReason.UserRequested, null);

            var sessionsDone = Task.WhenAll(pending.Select(session => session.Completion));
            Task all = Task.WhenAll(accepting ?? Task.CompletedTask, sessionsDone);
            Task finished = await Task.WhenAny(all, Task.Delay(options.StopTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, all))
                logger.LogWarning("TCP server {Name} did not finish closing within {Timeout} ms; the remaining sessions were aborted.", name, options.StopTimeout);

            lock (sync)
                SetStateLocked(ServerState.Stopped, null);

            logger.LogInformation("TCP server {Name} stopped.", name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stopping TCP server {Name} failed.", name);
            lock (sync)
                SetStateLocked(ServerState.Stopped, ex);
        }
    }

    // 停止进行中时，新的 StartAsync 等待它完成（派发上下文内不等待，以免与停止相互等待）。
    private async Task WaitForPendingStopAsync()
    {
        Task? pending;
        lock (sync)
            pending = stopTask;

        if (pending != null && !pending.IsCompleted && !IsInDispatchContext())
            await pending.ConfigureAwait(false);
    }

    // 等待任务，或等待取消令牌（取消只停止等待，任务仍会继续）。
    private static async Task WaitUnlessCancelledAsync(Task task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            Task finished = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
            if (!ReferenceEquals(finished, task))
                cancellationToken.ThrowIfCancellationRequested();
        }

        await task.ConfigureAwait(false);
    }

    // ————— 状态与事件派发 —————

    // 调用方持有 sync。状态变化与事件入队在同一临界区，保证事件顺序与状态变化顺序一致。
    private void SetStateLocked(ServerState next, Exception? exception)
    {
        ServerState previous = state;
        if (previous == next)
            return;

        state = next;
        var args = new ServerStateChangedEventArgs(previous, next, exception);
        EnqueueLocked(() => LifecycleSupport.InvokeEach(StateChanged, this, args, logger, "StateChanged"));
    }

    // 调用方持有 sync。事件进入单个派发循环的 FIFO 队列；派发循环在锁外逐个触发。
    private void EnqueueLocked(Action raise, TaskCompletionSource<bool>? delivered = null)
    {
        eventQueue.Enqueue(new QueuedEvent(raise, delivered));
        if (dispatching)
            return;

        dispatching = true;
        LifecycleSupport.StartDetached(DispatchEvents);
    }

    private Task DispatchEvents()
    {
        while (true)
        {
            QueuedEvent item;
            lock (sync)
            {
                if (eventQueue.Count == 0)
                {
                    dispatching = false;
                    return Task.CompletedTask;
                }

                item = eventQueue.Dequeue();
            }

            try
            {
                RaiseInDispatch(item.Raise);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatching a server event of TCP server {Name} failed.", name);
            }
            finally
            {
                item.Delivered?.TrySetResult(true);
            }
        }
    }

    // 会话的 FrameReceived 在路由的派发循环中触发：先会话级（sender 为会话），再服务端级（sender 为服务端）。两级都处于派发上下文中。
    private void RaiseSessionFrame(TcpSession session, FrameReceivedEventArgs args)
    {
        RaiseInDispatch(() =>
        {
            session.RaiseFrame(args, logger);
            var frame = new TcpSessionFrameEventArgs(session, args);
            LifecycleSupport.InvokeEach(FrameReceived, this, frame, logger, "FrameReceived");
        });
    }

    // 在派发上下文内执行处理器：标记随 AsyncLocal 流入处理器派生的续延，处理器返回后失效（计划 D16 的派发上下文做法）。
    private void RaiseInDispatch(Action raise)
    {
        var scope = new DispatchScope();
        dispatchScope.Value = scope;
        try
        {
            raise();
        }
        finally
        {
            scope.Exit();
        }
    }

    // 当前执行流是否位于本服务端某个事件处理器的执行期间。
    private bool IsInDispatchContext()
    {
        DispatchScope? scope = dispatchScope.Value;
        return scope != null && scope.IsActive;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(TcpServer));
    }

    // ————— 辅助 —————

    private static bool TryGetEndPoints(Socket socket, out IPEndPoint? remote, out IPEndPoint? local)
    {
        try
        {
            remote = socket.RemoteEndPoint as IPEndPoint;
            local = socket.LocalEndPoint as IPEndPoint;
            return true;
        }
        catch (SocketException)
        {
            remote = null;
            local = null;
            return false;
        }
        catch (ObjectDisposedException)
        {
            remote = null;
            local = null;
            return false;
        }
    }

    private bool IsAllowedRemote(IPEndPoint remote)
    {
        IPAddress[]? allowed = options.AllowedRemoteAddresses;
        if (allowed == null || allowed.Length == 0)
            return true;

        foreach (IPAddress candidate in allowed)
        {
            if (candidate.Equals(remote.Address))
                return true;
        }

        return false;
    }

    // 连接过滤器（用户代码）在服务端锁之外调用；异常视为拒绝。
    private bool FilterAccepts(IPEndPoint remote)
    {
        IConnectionFilter? filter = options.ConnectionFilter;
        if (filter == null)
            return true;

        try
        {
            return filter.Accept(remote);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The connection filter threw an exception; the connection from {Remote} is rejected.", remote);
            return false;
        }
    }

    // 广播的筛选条件（用户代码）：异常视为不选中该会话。
    private bool FilterAccepts(Func<ITcpSession, bool>? filter, TcpSession session)
    {
        if (filter == null)
            return true;

        try
        {
            return filter(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The broadcast filter threw an exception for session {SessionId}; the session is skipped.", session.Id);
            return false;
        }
    }

    private static void ObserveAbandoned(Task work)
        => work.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    // 一次派发的上下文标记。随 AsyncLocal 流入处理器派生的续延；处理器返回后失效。
    private sealed class DispatchScope
    {
        private int active = 1;

        public bool IsActive => Volatile.Read(ref active) != 0;

        public void Exit() => Volatile.Write(ref active, 0);
    }

    // 派发队列中的一个事件：raise 在派发上下文中执行；delivered 在执行之后完成（用于等待 SessionConnected 的派发）。
    private sealed class QueuedEvent
    {
        public QueuedEvent(Action raise, TaskCompletionSource<bool>? delivered)
        {
            Raise = raise;
            Delivered = delivered;
        }

        public Action Raise { get; }

        public TaskCompletionSource<bool>? Delivered { get; }
    }
}
