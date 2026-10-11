using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
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
/// 配置在构造时校验（D5），运行行为只依赖构造时的快照；<see cref="Config"/> 返回的是构造时传入的对象本身。启用 TLS 时服务端证书在构造时加载（找不到或没有私钥则抛出 <see cref="ArgumentException"/>），并在 <see cref="DisposeAsync"/> 中释放（组件提供的证书除外）。
/// 共享状态与各职责由内部组件承担：监听器循环（<c>TcpListenerLoop</c>）、接入检查（<c>TcpSessionAdmission</c>）、会话握手（<c>TcpSessionHandshake</c>）、
/// 会话生命周期（<c>TcpSessionLifecycle</c>）与事件派发（<c>ServerEventDispatcher</c>）。
/// </remarks>
public sealed class TcpServer : ITcpServer
{
    // 释放时在 StopTimeout 之外额外等待的毫秒数（见 Dispose）。
    private const int DisposeGraceMilliseconds = 1000;

    private readonly TcpServerConfig config;
    private readonly TcpServerOptions options;
    private readonly string name;
    private readonly ILogger logger;
    private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
    private readonly TcpServerCore core;
    private readonly TcpListenerLoop listenerLoop;
    private readonly TcpSessionAdmission admission;
    private readonly TcpSessionLifecycle lifecycle;
    private readonly X509Certificate2? serverCertificate;
    private readonly bool ownsServerCertificate;
    private int disposed;

    /// <summary>
    /// 创建 TCP 服务端（尚未监听）。构造时校验配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，调用方的配置对象不会被修改。
    /// 名称默认为 <c>ListenAddress:Port</c>。
    /// </summary>
    /// <param name="config">服务端配置。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（分帧、关联、握手、心跳、连接过滤器）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public TcpServer(TcpServerConfig config, ILogger<TcpServer>? logger = null, TcpChannelComponents? components = null)
        : this(config, logger, components, SocketTcpListener.Bind)
    {
    }

    /// <summary>
    /// 创建使用指定监听器工厂的 TCP 服务端（内部，供测试注入监听器）。构造与校验规则同公开构造函数。
    /// </summary>
    /// <param name="config">服务端配置。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖；为 null 时只使用配置。</param>
    /// <param name="listenerFactory">绑定监听器的工厂。</param>
    internal TcpServer(TcpServerConfig config, ILogger<TcpServer>? logger, TcpChannelComponents? components, TcpListenerFactory listenerFactory)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        options = new TcpServerOptions(config, components);
        name = $"{config.ListenAddress}:{config.Port}";
        this.logger = logger ?? NullLogger<TcpServer>.Instance;

        if (options.TlsEnabled)
        {
            ownsServerCertificate = options.ComponentServerCertificate == null;
            serverCertificate = options.ComponentServerCertificate
                ?? CertificateLoader.Load(options.ServerCertificateSource!, "Tls.ServerCertificate");
        }

        core = new TcpServerCore(name, this.logger, RaiseStateChanged);
        listenerLoop = new TcpListenerLoop(options, listenerFactory, name, this.logger);
        lifecycle = new TcpSessionLifecycle(core, options, this.logger, serverCertificate, RaiseSessionConnected, RaiseSessionClosed, RaiseSessionFrame);
        admission = new TcpSessionAdmission(core, options, this.logger, this, lifecycle);
    }

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public TcpServerConfig Config => config;

    /// <inheritdoc />
    public ServerState State => core.State;

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint
    {
        get
        {
            lock (core.Sync)
                return core.LocalEndPoint;
        }
    }

    /// <inheritdoc />
    public int SessionCount
    {
        get
        {
            lock (core.Sync)
                return core.Sessions.Count;
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ITcpSession> Sessions
    {
        get
        {
            lock (core.Sync)
                return core.Sessions.Values.Cast<ITcpSession>().ToList();
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

            lock (core.Sync)
            {
                if (core.State != ServerState.Stopped)
                    return CommResult.Fail("The server is already started.", CommErrorKind.InvalidRequest);

                core.StopRequested = false;
                core.StopTask = null;
                core.SetStateLocked(ServerState.Starting, null);
            }

            CommResult<ITcpListener> bound = listenerLoop.Bind();
            if (!bound.IsSuccess)
            {
                lock (core.Sync)
                {
                    if (!core.StopRequested)
                        core.SetStateLocked(ServerState.Stopped, bound.Exception);
                }

                logger.LogWarning("Starting TCP server {Name} failed: {Result}", name, bound);
                return bound.ToResult();
            }

            lock (core.Sync)
            {
                if (core.StopRequested)
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
        if (core.Dispatcher.IsInDispatchContext())
            return Task.CompletedTask;

        return TcpServerCore.WaitUnlessCancelledAsync(stop, cancellationToken);
    }

    /// <inheritdoc />
    public bool TryGetSession(long sessionId, [NotNullWhen(true)] out ITcpSession? session)
    {
        lock (core.Sync)
        {
            if (core.Sessions.TryGetValue(sessionId, out TcpSession? found))
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
        lock (core.Sync)
            core.Sessions.TryGetValue(sessionId, out session);

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
        lock (core.Sync)
            targets = core.Sessions.Values.ToList();

        var sends = new List<Task<CommResult>>(targets.Count);
        foreach (TcpSession session in targets)
        {
            if (!session.IsConnected || !admission.FilterBroadcast(filter, session))
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
        int limit = options.StopTimeout + DisposeGraceMilliseconds;
        if (!disposing.Wait(limit))
            logger.LogWarning("Disposing TCP server {Name} did not finish within {Timeout} ms; the stop continues in the background.", name, limit);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        Task stop = BeginStop();
        if (core.Dispatcher.IsInDispatchContext())
        {
            // 派发上下文内不等待停止：证书在停止完成后释放（停止本身不会因此等待）。
            _ = stop.ContinueWith(_ => ReleaseServerCertificate(), TaskScheduler.Default);
            return default;
        }

        return new ValueTask(ReleaseAfterStopAsync(stop));
    }

    // 显式关闭会话（ITcpSession.CloseAsync 经此进入会话生命周期）。
    internal Task CloseSessionAsync(TcpSession session, CancellationToken cancellationToken)
        => lifecycle.CloseSessionAsync(session, cancellationToken);

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

    // 调用方持有服务端锁。把绑定好的监听器交给接受循环，状态进入 Running。
    private void StartListening(ITcpListener listener)
    {
        core.Listener = listener;
        core.LocalEndPoint = listener.LocalEndPoint;
        core.SetStateLocked(ServerState.Running, null);
        core.AcceptLoop = LifecycleSupport.StartDetached(() => listenerLoop.AcceptAsync(listener, admission.Admit, OnListenerFailed));
    }

    // 接受循环出现非停止原因的异常：状态进入 Faulted；启用 RestartOnFault 时在后台重新监听。停止过程中的异常不算故障。
    private void OnListenerFailed(ITcpListener listener, Exception exception)
    {
        bool faulted;
        lock (core.Sync)
        {
            faulted = !core.StopRequested && ReferenceEquals(core.Listener, listener);
            if (faulted)
            {
                core.Listener = null;
                core.LocalEndPoint = null;
                core.AcceptLoop = null;
                core.SetStateLocked(ServerState.Faulted, exception);

                if (options.RestartPolicy != null)
                {
                    core.RestartSource = new CancellationTokenSource();
                    CancellationToken token = core.RestartSource.Token;
                    IBackoffPolicy policy = options.RestartPolicy;
                    LifecycleSupport.StartDetached(() => listenerLoop.RestartAsync(policy, token, TryStartRestartedListener));
                }
            }
        }

        listener.Dispose();
        if (faulted)
            logger.LogWarning(exception, "The listener of TCP server {Name} failed; the server is faulted.", name);
    }

    // 重新监听成功时在服务端锁下启动新监听器；服务端已停止则返回 false（调用方释放新监听器）。
    private bool TryStartRestartedListener(ITcpListener listener)
    {
        lock (core.Sync)
        {
            if (core.StopRequested)
                return false;

            core.RestartSource = null;
            StartListening(listener);
            return true;
        }
    }

    // 停止（计划 11.2）：Stopping → 关闭监听器 → 并行拆除全部会话（UserRequested）→ 等待接受循环与会话拆除完成（至多 StopTimeout）→ Stopped。
    // 在后台执行，由调用方等待；派发上下文内的调用方不等待。只有第一次调用启动停止，之后的调用等待同一次停止。
    private Task BeginStop()
    {
        lock (core.Sync)
        {
            if (core.StopTask != null)
                return core.StopTask;

            if (core.State == ServerState.Stopped)
                return Task.CompletedTask;

            core.StopRequested = true;
            core.StopTask = LifecycleSupport.StartDetached(StopCoreAsync);
            return core.StopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            ITcpListener? closingListener;
            Task? accepting;
            CancellationTokenSource? restart;
            List<TcpSession> pending;
            lock (core.Sync)
            {
                core.SetStateLocked(ServerState.Stopping, null);
                closingListener = core.Listener;
                core.Listener = null;
                core.LocalEndPoint = null;
                accepting = core.AcceptLoop;
                core.AcceptLoop = null;
                restart = core.RestartSource;
                core.RestartSource = null;
                pending = core.Live.ToList();
            }

            restart?.Cancel();
            closingListener?.Dispose();

            foreach (TcpSession session in pending)
                lifecycle.ScheduleTeardown(session, DisconnectReason.UserRequested, null);

            var sessionsDone = Task.WhenAll(pending.Select(session => session.Completion));
            Task all = Task.WhenAll(accepting ?? Task.CompletedTask, sessionsDone);
            Task finished = await Task.WhenAny(all, Task.Delay(options.StopTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, all))
                logger.LogWarning("TCP server {Name} did not finish closing within {Timeout} ms; the remaining sessions were aborted.", name, options.StopTimeout);

            lock (core.Sync)
                core.SetStateLocked(ServerState.Stopped, null);

            logger.LogInformation("TCP server {Name} stopped.", name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stopping TCP server {Name} failed.", name);
            lock (core.Sync)
                core.SetStateLocked(ServerState.Stopped, ex);
        }
    }

    // 停止进行中时，新的 StartAsync 等待它完成（派发上下文内不等待，以免与停止相互等待）。
    private async Task WaitForPendingStopAsync()
    {
        Task? pending;
        lock (core.Sync)
            pending = core.StopTask;

        if (pending != null && !pending.IsCompleted && !core.Dispatcher.IsInDispatchContext())
            await pending.ConfigureAwait(false);
    }

    // 以下触发器由共享状态与会话生命周期回调（发送者为服务端）。
    private void RaiseStateChanged(ServerStateChangedEventArgs args)
        => LifecycleSupport.InvokeEach(StateChanged, this, args, logger, "StateChanged");

    private void RaiseSessionConnected(TcpSessionEventArgs args)
        => LifecycleSupport.InvokeEach(SessionConnected, this, args, logger, "SessionConnected");

    private void RaiseSessionClosed(TcpSessionClosedEventArgs args)
        => LifecycleSupport.InvokeEach(SessionClosed, this, args, logger, "SessionClosed");

    // 会话的 FrameReceived 在路由的派发循环中触发：先会话级（sender 为会话），再服务端级（sender 为服务端）。两级都处于派发上下文中。
    private void RaiseSessionFrame(TcpSession session, FrameReceivedEventArgs args)
    {
        core.Dispatcher.RaiseInDispatch(() =>
        {
            session.RaiseFrame(args, logger);
            var frame = new TcpSessionFrameEventArgs(session, args);
            LifecycleSupport.InvokeEach(FrameReceived, this, frame, logger, "FrameReceived");
        });
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(TcpServer));
    }
}
