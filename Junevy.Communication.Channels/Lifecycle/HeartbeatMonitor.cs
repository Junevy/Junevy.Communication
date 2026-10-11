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

    private readonly HeartbeatProbeRunner probeRunner;
    private readonly bool probingEnabled;
    private readonly int probeInterval;
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

        probeInterval = options.Interval;
        maxFailures = options.MaxFailures;
        onlyWhenIdle = options.OnlyWhenIdle;
        this.idleTimeout = idleTimeout;
        this.statistics = statistics;
        this.onDead = onDead;
        this.logger = logger;
        probeRunner = new HeartbeatProbeRunner(probe, options.Timeout, logger);
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

                if (idleTimeout > 0 && IdleExpired(now, ref inboundAt, ref lastInbound))
                    return;

                if (!probingEnabled || now < nextProbeAt)
                    continue;

                nextProbeAt = now + ToTicks(probeInterval);

                // 仅空闲时探测：上一次判定之后出现过收发则跳过本次探测。
                if (onlyWhenIdle && Traffic() != lastTraffic)
                {
                    lastTraffic = Traffic();
                    continue;
                }

                HeartbeatProbeRunner.ProbeOutcome outcome = await probeRunner.ProbeOnceAsync(stopToken).ConfigureAwait(false);
                stopToken.ThrowIfCancellationRequested();

                // 探测自身的收发不计为业务流量，因此在探测结束后重新取样。
                lastTraffic = Traffic();
                if (JudgeProbe(outcome))
                    return;
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // 监视器被停止。
        }
    }

    // 空闲判定：有入站帧则重新计时；超过空闲超时则报告 IdleTimeout 并返回 true（监视结束）。
    private bool IdleExpired(long now, ref long inboundAt, ref long lastInbound)
    {
        long inbound = statistics.FramesReceived;
        if (inbound != lastInbound)
        {
            lastInbound = inbound;
            inboundAt = now;
            return false;
        }

        if (now - inboundAt < ToTicks(idleTimeout))
            return false;

        logger.LogWarning("No inbound frame arrived within {Timeout} ms; the connection is considered dead.", idleTimeout);
        Report(DisconnectReason.IdleTimeout);
        return true;
    }

    // 判定一次探测结果：成功则重置连续失败；未连接与通道忙不计入失败；失败次数达到阈值时报告 HeartbeatFailed 并返回 true（监视结束）。
    private bool JudgeProbe(HeartbeatProbeRunner.ProbeOutcome outcome)
    {
        CommResult result = outcome.Result;
        if (result.IsSuccess)
        {
            statistics.ResetConsecutiveHeartbeatFailures();
            return false;
        }

        // 尚未连接的探测说明连接正在被关闭或重建，由生命周期处理，不计入心跳失败。
        if (result.ErrorKind == CommErrorKind.NotConnected)
            return false;

        // 通道忙：探测排在请求锁或发送锁上直到超时，没有写出任何帧。这不是对端沉默的证据，不计入心跳失败，也不影响连续失败计数。
        if (outcome.ChannelBusy)
        {
            logger.LogDebug("Heartbeat probe skipped: channel busy ({Result}).", result);
            return false;
        }

        int failures = statistics.IncrementConsecutiveHeartbeatFailures();
        logger.LogWarning("Heartbeat probe failed ({Failures} of {MaxFailures}): {Result}", failures, maxFailures, result);
        if (failures < maxFailures)
            return false;

        Report(DisconnectReason.HeartbeatFailed);
        return true;
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
