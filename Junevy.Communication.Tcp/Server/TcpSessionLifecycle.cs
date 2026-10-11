using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 会话的生命周期（P25 Task 13，补审第 2 节）：应用套接字选项 → 握手（<see cref="TcpSessionHandshake"/>）→ 加入会话表并排队 <c>SessionConnected</c>
/// → 释放握手积压 → 启动心跳；拆除（每个会话只执行一次）：移出会话表 → 停止心跳与通道 → 排队 <c>SessionClosed</c>。
/// 事件经服务端的派发器按顺序触发；处理器由 <see cref="TcpServer"/> 以回调提供。
/// </summary>
internal sealed class TcpSessionLifecycle
{
    // 填充循环每次申请的缓冲大小（字节）。服务端配置未提供该项，与客户端的默认值一致。
    private const int ReceiveBufferSize = 4096;

    private readonly TcpServerCore core;
    private readonly TcpServerOptions options;
    private readonly ILogger logger;
    private readonly Action<TcpSessionEventArgs> raiseConnected;
    private readonly Action<TcpSessionClosedEventArgs> raiseClosed;
    private readonly Action<TcpSession, FrameReceivedEventArgs> raiseFrame;
    private readonly TcpSessionHandshake handshake;

    /// <summary>创建会话生命周期。</summary>
    /// <param name="core">服务端的共享状态。</param>
    /// <param name="options">服务端的已校验选项。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="serverCertificate">服务端证书（启用 TLS 时）。</param>
    /// <param name="raiseConnected">触发 <c>SessionConnected</c> 的回调。</param>
    /// <param name="raiseClosed">触发 <c>SessionClosed</c> 的回调。</param>
    /// <param name="raiseFrame">触发会话的 <c>FrameReceived</c>（会话级与服务端级）的回调。</param>
    public TcpSessionLifecycle(TcpServerCore core, TcpServerOptions options, ILogger logger, X509Certificate2? serverCertificate,
                               Action<TcpSessionEventArgs> raiseConnected, Action<TcpSessionClosedEventArgs> raiseClosed,
                               Action<TcpSession, FrameReceivedEventArgs> raiseFrame)
    {
        this.core = core;
        this.options = options;
        this.logger = logger;
        this.raiseConnected = raiseConnected;
        this.raiseClosed = raiseClosed;
        this.raiseFrame = raiseFrame;
        handshake = new TcpSessionHandshake(options, logger, serverCertificate, OpenChannel);
    }

    /// <summary>
    /// 会话的完整生命周期：建立失败（握手失败或超时、未加入）→ 静默拆除；异常 → 拆除（已加入则派发 SessionClosed）。
    /// </summary>
    /// <param name="session">已接受的会话。</param>
    /// <returns>生命周期结束的任务。</returns>
    public async Task RunSessionAsync(TcpSession session)
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

    /// <summary>
    /// 故障与停止的入口（可从任意线程、包括解析循环与心跳计时器调用）：只调度后台拆除，不等待，同一会话的重复调度无害。
    /// </summary>
    /// <param name="session">要拆除的会话。</param>
    /// <param name="reason">拆除原因。</param>
    /// <param name="exception">导致拆除的异常；可为 null。</param>
    public void ScheduleTeardown(TcpSession session, DisconnectReason reason, Exception? exception)
    {
        if (session.Closing)
            return;

        LifecycleSupport.StartDetached(() => TeardownAsync(session, reason, exception));
    }

    /// <summary>
    /// 显式关闭会话（ITcpSession.CloseAsync）。在派发上下文内只发出信号；否则等待拆除完成（可取消）。
    /// </summary>
    /// <param name="session">要关闭的会话。</param>
    /// <param name="cancellationToken">取消令牌（只停止等待，拆除仍会继续）。</param>
    /// <returns>拆除完成（或派发上下文内的信号）的任务。</returns>
    public Task CloseSessionAsync(TcpSession session, CancellationToken cancellationToken)
    {
        ScheduleTeardown(session, DisconnectReason.UserRequested, null);
        if (core.Dispatcher.IsInDispatchContext())
            return Task.CompletedTask;

        return TcpServerCore.WaitUnlessCancelledAsync(session.Completion, cancellationToken);
    }

    // 返回 true 表示会话已加入会话表（之后的拆除由故障或停止发起）；false 表示未加入，调用方负责静默拆除。
    private async Task<bool> SetupSessionAsync(TcpSession session)
    {
        handshake.ApplySocketOptions(session);
        CommResult handshakeResult = await handshake.RunHandshakeAsync(session).ConfigureAwait(false);
        if (!handshakeResult.IsSuccess)
        {
            logger.LogWarning("Session {SessionId} from {Remote} did not complete its handshake: {Result}", session.Id, session.RemoteEndPoint, handshakeResult);
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

    // 创建关联表与路由，BeginHandshake，启动通道（计划 11.2 的顺序）。全部在服务端锁下完成，以便与拆除互斥：拆除之后不会再创建通道。
    private bool OpenChannel(TcpSession session, Stream stream)
    {
        lock (core.Sync)
        {
            if (session.Closing)
                return false;

            var table = new PendingRequestTable(options.Correlation, options.KeyExtractor, options.LateReplyWindow, logger);
            table.BeginHandshake();

            var router = new FrameRouter(table, options.ReceiveQueueCapacity, QueueFullMode.Wait,
                args =>
                {
                    raiseFrame(session, args);
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

    // 加入会话表并排队 SessionConnected（同一临界区，保证拆除只能在加入之后排队 SessionClosed）。
    private bool Join(TcpSession session)
    {
        lock (core.Sync)
        {
            if (core.StopRequested || session.Closing)
                return false;

            session.MarkJoined(DateTimeOffset.UtcNow);
            core.Sessions.Add(session.Id, session);

            var args = new TcpSessionEventArgs(session);
            core.Dispatcher.EnqueueLocked(() => raiseConnected(args), session.ConnectedDelivered);
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
        lock (core.Sync)
        {
            if (session.Closing)
                return;

            var monitor = new HeartbeatMonitor(probe, options.Heartbeat, options.SessionIdleTimeout, session.Statistics,
                reason => ScheduleTeardown(session, reason, null), logger);

            session.Heartbeat = monitor;
            monitor.Start();
        }
    }

    // 会话自己的心跳探测：HealthProbeFactory 以该会话为参数创建的专用探测；未设置工厂时使用绑定该会话通道的内置负载探测。
    private IHealthProbe CreateSessionProbe(TcpSession session)
    {
        Func<IByteChannel, IHealthProbe>? factory = options.HealthProbeFactory;
        if (factory == null)
            return new PayloadHeartbeatProbe(session.View!, options.HeartbeatPayload!, options.HeartbeatExpectedReply, options.Heartbeat.Timeout);

        IHealthProbe? probe = factory(session);
        if (probe == null)
            throw new InvalidOperationException("HealthProbeFactory returned null.");

        return probe;
    }

    // 拆除会话（每个会话只执行一次）：从会话表移除 → 停止心跳 → 停止通道（中止传输、按 StopTimeout 排空）→ 已加入则排队 SessionClosed → 标记完成。
    private async Task TeardownAsync(TcpSession session, DisconnectReason reason, Exception? exception)
    {
        HeartbeatMonitor? monitor;
        StreamChannel? channel;
        bool joined;
        lock (core.Sync)
        {
            if (session.Closing)
                return;

            session.BeginClosing();
            core.ActiveSessions--;
            joined = session.Joined;
            if (joined)
                core.Sessions.Remove(session.Id);

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
            lock (core.Sync)
            {
                core.Live.Remove(session);
                if (joined)
                {
                    var args = new TcpSessionClosedEventArgs(session, reason, exception);
                    core.Dispatcher.EnqueueLocked(() => raiseClosed(args));
                }
            }

            logger.LogInformation("Session {SessionId} from {Remote} closed: {Reason}.", session.Id, session.RemoteEndPoint, reason);
            session.CompleteClosing();
        }
    }
}
