using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 连接级的心跳与空闲监视（设计文档 5.4 节，计划 9.1）。
/// 启用心跳时按 <see cref="HeartbeatOptions.Interval"/> 探测（可仅在空闲时探测）；连续失败达到 <see cref="HeartbeatOptions.MaxFailures"/> 时报告 <c>HeartbeatFailed</c>；
/// 空闲超过 idleTimeout 时报告 <c>IdleTimeout</c>。每个监视器最多报告一次死亡，报告后停止。
/// </summary>
/// <remarks>
/// 死亡回调（构造参数 <c>onDead</c>）在监视循环的线程上调用，必须立即返回（例如只调度后台处理）。<see cref="StopAsync"/> 只能在后台路径调用，不能在死亡回调内等待。
/// </remarks>
internal sealed class HeartbeatMonitor : IAsyncDisposable
{
    // 循环的最长唤醒间隔：空闲超时与探测间隔都以此为粒度判定，避免长时间休眠后错过截止时间。
    private const int MaxWakeMilliseconds = 250;

    private readonly IHealthProbe? probe;
    private readonly bool probingEnabled;
    private readonly int probeInterval;
    private readonly int probeTimeout;
    private readonly int maxFailures;
    private readonly bool onlyWhenIdle;
    private readonly int idleTimeout;
    private readonly ConnectionStatistics statistics;
    private readonly Action<DisconnectReason> onDead;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stopSource = new CancellationTokenSource();
    private int started;
    private Task? loop;

    /// <summary>
    /// 创建心跳监视器（未启动）。
    /// </summary>
    /// <param name="probe">健康探测；启用心跳时不能为 null。</param>
    /// <param name="options">心跳配置（复制其数值，之后修改不影响本实例）。</param>
    /// <param name="idleTimeout">空闲超时（毫秒）；0 表示禁用。</param>
    /// <param name="statistics">连接统计（读取收发计数，记录心跳失败）。</param>
    /// <param name="onDead">连接判定为死亡时的回调（原因）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="ArgumentException">启用心跳但没有探测。</exception>
    /// <exception cref="ArgumentOutOfRangeException">间隔、超时、失败次数或空闲超时非法。</exception>
    public HeartbeatMonitor(IHealthProbe? probe, HeartbeatOptions options, int idleTimeout, ConnectionStatistics statistics,
                            Action<DisconnectReason> onDead, ILogger logger)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));
        if (statistics == null)
            throw new ArgumentNullException(nameof(statistics));
        if (onDead == null)
            throw new ArgumentNullException(nameof(onDead));
        if (logger == null)
            throw new ArgumentNullException(nameof(logger));
        if (idleTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), idleTimeout, "The idle timeout must not be negative.");

        probingEnabled = options.Enabled;
        if (probingEnabled)
        {
            if (probe == null)
                throw new ArgumentException("A health probe is required when heartbeat is enabled.", nameof(probe));
            if (options.Interval <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), options.Interval, "The heartbeat interval must be positive.");
            if (options.Timeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), options.Timeout, "The heartbeat timeout must be positive.");
            if (options.MaxFailures < 1)
                throw new ArgumentOutOfRangeException(nameof(options), options.MaxFailures, "The heartbeat failure threshold must be at least 1.");
        }

        this.probe = probe;
        probeInterval = options.Interval;
        probeTimeout = options.Timeout;
        maxFailures = options.MaxFailures;
        onlyWhenIdle = options.OnlyWhenIdle;
        this.idleTimeout = idleTimeout;
        this.statistics = statistics;
        this.onDead = onDead;
        this.logger = logger;
    }

    /// <summary>
    /// 启动监视循环。启用心跳或空闲超时时才真正运行；只能调用一次。
    /// </summary>
    /// <exception cref="InvalidOperationException">已经启动。</exception>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The heartbeat monitor has already been started.");

        if (!probingEnabled && idleTimeout == 0)
            return;

        loop = LifecycleSupport.StartDetached(() => RunAsync(stopSource.Token));
    }

    /// <summary>
    /// 停止监视循环并等待其退出。可重复调用。
    /// </summary>
    /// <returns>循环退出的任务。</returns>
    public async ValueTask StopAsync()
    {
        stopSource.Cancel();
        Task? running = loop;
        if (running != null)
            await running.ConfigureAwait(false);
    }

    /// <summary>等同于 <see cref="StopAsync"/>。</summary>
    /// <returns>停止完成的任务。</returns>
    public ValueTask DisposeAsync() => StopAsync();

    private async Task RunAsync(CancellationToken stopToken)
    {
        try
        {
            long inboundAt = Stopwatch.GetTimestamp();
            long lastInbound = statistics.FramesReceived;
            long lastTraffic = Traffic();
            long nextProbeAt = inboundAt + ToTicks(probeInterval);

            while (true)
            {
                long wakeAt = probingEnabled ? nextProbeAt : long.MaxValue;
                if (idleTimeout > 0)
                    wakeAt = Math.Min(wakeAt, inboundAt + ToTicks(idleTimeout));

                await Task.Delay(MillisecondsUntil(wakeAt), stopToken).ConfigureAwait(false);
                long now = Stopwatch.GetTimestamp();

                if (idleTimeout > 0)
                {
                    long inbound = statistics.FramesReceived;
                    if (inbound != lastInbound)
                    {
                        lastInbound = inbound;
                        inboundAt = now;
                    }
                    else if (now - inboundAt >= ToTicks(idleTimeout))
                    {
                        logger.LogWarning("No inbound frame arrived within {Timeout} ms; the connection is considered dead.", idleTimeout);
                        Report(DisconnectReason.IdleTimeout);
                        return;
                    }
                }

                if (!probingEnabled || now < nextProbeAt)
                    continue;

                nextProbeAt = now + ToTicks(probeInterval);

                // 仅空闲时探测：上一次判定之后出现过收发则跳过本次探测。
                if (onlyWhenIdle && Traffic() != lastTraffic)
                {
                    lastTraffic = Traffic();
                    continue;
                }

                ProbeOutcome outcome = await ProbeOnceAsync(stopToken).ConfigureAwait(false);
                stopToken.ThrowIfCancellationRequested();

                // 探测自身的收发不计为业务流量，因此在探测结束后重新取样。
                lastTraffic = Traffic();

                CommResult result = outcome.Result;
                if (result.IsSuccess)
                {
                    statistics.ResetConsecutiveHeartbeatFailures();
                    continue;
                }

                // 尚未连接的探测说明连接正在被关闭或重建，由生命周期处理，不计入心跳失败。
                if (result.ErrorKind == CommErrorKind.NotConnected)
                    continue;

                // 通道忙：探测排在请求锁或发送锁上直到超时，没有写出任何帧。这不是对端沉默的证据，不计入心跳失败，也不影响连续失败计数。
                if (outcome.ChannelBusy)
                {
                    logger.LogDebug("Heartbeat probe skipped: channel busy ({Result}).", result);
                    continue;
                }

                int failures = statistics.IncrementConsecutiveHeartbeatFailures();
                logger.LogWarning("Heartbeat probe failed ({Failures} of {MaxFailures}): {Result}", failures, maxFailures, result);
                if (failures >= maxFailures)
                {
                    Report(DisconnectReason.HeartbeatFailed);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // 监视器被停止。
        }
    }

    // 单次探测：探测在超时后被放弃（取消）；探测本身忽略取消令牌时，也不会让循环无限等待。
    // 结果同时带回探测上下文的登记：探测是否因通道忙而未写出任何帧（见 HeartbeatProbeScope.ChannelBusy）。
    private async Task<ProbeOutcome> ProbeOnceAsync(CancellationToken stopToken)
    {
        using (CancellationTokenSource probeSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
        {
            (Task<CommResult> probeTask, HeartbeatProbeScope scope) = StartProbe(probeSource.Token);
            Task timeout = Task.Delay(probeTimeout, stopToken);
            Task finished = await Task.WhenAny(probeTask, timeout).ConfigureAwait(false);
            if (ReferenceEquals(finished, probeTask))
            {
                CommResult completed = await probeTask.ConfigureAwait(false);
                return new ProbeOutcome(completed, scope.ChannelBusy);
            }

            probeSource.Cancel();
            var timedOut = CommResult.Fail($"No heartbeat reply was received within {probeTimeout} ms.", CommErrorKind.Timeout);
            return new ProbeOutcome(timedOut, scope.ChannelBusy);
        }
    }

    // 在心跳探测上下文中启动探测（见 HeartbeatProbeScope）：上下文只在探测启动的同步部分内挂到监视循环的执行流上，
    // 返回后恢复原有上下文；探测自身的续延已捕获该上下文，直到探测返回为止都享有豁免。调用方凭返回的上下文读取探测的登记。
    private (Task<CommResult> Probe, HeartbeatProbeScope Scope) StartProbe(CancellationToken probeToken)
    {
        using (HeartbeatProbeScope scope = HeartbeatProbeScope.Enter())
        {
            return (ProbeSafelyAsync(probeToken, scope), scope);
        }
    }

    // 单次探测的结果，以及探测是否因通道忙而未写出任何帧（见 HeartbeatProbeScope.ChannelBusy）。
    private readonly struct ProbeOutcome
    {
        public ProbeOutcome(CommResult result, bool channelBusy)
        {
            Result = result;
            ChannelBusy = channelBusy;
        }

        public CommResult Result { get; }

        public bool ChannelBusy { get; }
    }

    private async Task<CommResult> ProbeSafelyAsync(CancellationToken probeToken, HeartbeatProbeScope scope)
    {
        try
        {
            return await probe!.ProbeAsync(probeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (probeToken.IsCancellationRequested)
        {
            return CommResult.Fail("The heartbeat probe was cancelled.", CommErrorKind.Cancelled);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The heartbeat probe threw an exception.");
            return CommResult.Fail("The heartbeat probe threw an exception.", CommErrorKind.Unspecified, null, ex);
        }
        finally
        {
            scope.Close();
        }
    }

    private void Report(DisconnectReason reason)
    {
        try
        {
            onDead(reason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The heartbeat death callback threw an exception.");
        }
    }

    // 收发帧的总数：任何变化都表示链路有业务流量。
    private long Traffic() => statistics.FramesSent + statistics.FramesReceived;

    private static int MillisecondsUntil(long timestamp)
    {
        long remaining = timestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0)
            return 1;

        double milliseconds = Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency);
        return (int)Math.Max(1d, Math.Min(MaxWakeMilliseconds, milliseconds));
    }

    private static long ToTicks(int milliseconds) => (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
}
