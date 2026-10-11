using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 心跳的单次探测（P25 Task 12，补审第 6 节）：在超时内运行探测；超时或取消后放弃探测（探测本身可能忽略取消令牌），
/// 并回报探测是否因通道忙而未写出任何帧（见 <see cref="HeartbeatProbeScope"/>）。由 <see cref="HeartbeatMonitor"/> 的监视循环调用。
/// </summary>
internal sealed class HeartbeatProbeRunner
{
    private readonly IHealthProbe? probe;
    private readonly int probeTimeout;
    private readonly ILogger logger;

    /// <summary>创建探测运行器。</summary>
    /// <param name="probe">健康探测；启用心跳时不能为 null。</param>
    /// <param name="probeTimeout">单次探测的超时（毫秒）。</param>
    /// <param name="logger">日志记录器。</param>
    public HeartbeatProbeRunner(IHealthProbe? probe, int probeTimeout, ILogger logger)
    {
        this.probe = probe;
        this.probeTimeout = probeTimeout;
        this.logger = logger;
    }

    /// <summary>单次探测的结果，以及探测是否因通道忙而未写出任何帧（见 <see cref="HeartbeatProbeScope.ChannelBusy"/>）。</summary>
    internal readonly struct ProbeOutcome
    {
        public ProbeOutcome(CommResult result, bool channelBusy)
        {
            Result = result;
            ChannelBusy = channelBusy;
        }

        public CommResult Result { get; }

        public bool ChannelBusy { get; }
    }

    // 单次探测：探测在超时后被放弃（取消）；探测本身忽略取消令牌时，也不会让循环无限等待。
    // 结果同时带回探测上下文的登记：探测是否因通道忙而未写出任何帧（见 HeartbeatProbeScope.ChannelBusy）。
    public async Task<ProbeOutcome> ProbeOnceAsync(CancellationToken stopToken)
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
}
