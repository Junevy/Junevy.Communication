using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 后台重连循环（P61-D9）。**不是**通用的"重试一个操作"，因此不放在 Core：它带状态迁移与恢复订阅。
/// <para>退避用 Core 的 <see cref="ExponentialBackoff"/>，不限次数，每次尝试前先等待。
/// 不在 SDK 的事件回调里发起重连——MQTTnet 明确警告这样会死锁，因此由 <see cref="MqttClient"/> 在事件回调里
/// 只是"启动"这个循环，循环本身跑在自己的任务上。</para>
/// <para>每一轮有自己的取消令牌源：<see cref="StopAsync"/> 只结束当前这一轮，之后仍可再次启动（应用断开后又连上，
/// 下一次断线照样要重连）；只有 <see cref="DisposeAsync"/> 才永久停止。</para>
/// </summary>
internal sealed class MqttReconnectLoop : IAsyncDisposable
{
    private readonly IBackoffPolicy backoff;
    private readonly Func<CancellationToken, Task<CommResult>> attemptAsync;
    private readonly Action<int, CommResult> onFailure;
    private readonly ILogger logger;
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? running;
    private bool disposed;

    /// <summary>创建重连循环。</summary>
    /// <param name="backoff">退避策略；返回 null 表示放弃。</param>
    /// <param name="attemptAsync">一次连接尝试；成功即表示重连完成，抛 <see cref="OperationCanceledException"/> 表示这一轮就此结束。</param>
    /// <param name="onFailure">每次失败后的回调，参数为已尝试的次数与结果。</param>
    /// <param name="logger">日志。</param>
    public MqttReconnectLoop(
        IBackoffPolicy backoff,
        Func<CancellationToken, Task<CommResult>> attemptAsync,
        Action<int, CommResult> onFailure,
        ILogger logger)
    {
        this.backoff = backoff;
        this.attemptAsync = attemptAsync;
        this.onFailure = onFailure;
        this.logger = logger;
    }

    /// <summary>启动一轮重连。已经有一轮在跑、或者已经释放时返回 false。</summary>
    /// <returns>本次调用真正启动了一轮时为 true。</returns>
    public bool TryStart()
    {
        lock (sync)
        {
            if (running is not null || disposed)
            {
                return false;
            }

            var run = new CancellationTokenSource();
            cancellation = run;
            running = Task.Run(() => RunAsync(run));
            return true;
        }
    }

    /// <summary>结束当前这一轮（如果有）并等待它退出。之后仍可再次启动。</summary>
    /// <returns>这一轮已退出。</returns>
    public async Task StopAsync()
    {
        Task? pending;
        CancellationTokenSource? run;
        lock (sync)
        {
            pending = running;
            run = cancellation;
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            // 取消会让正在等待退避、等锁或正在连接的那次尝试结束，这一轮随即退出。
            run!.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 这一轮恰好自己结束并释放了令牌源：没有需要取消的了。
        }

        await pending.ConfigureAwait(false);
    }

    /// <summary>永久停止：结束当前这一轮并等待它退出，之后 <see cref="TryStart"/> 一律返回 false。可重复调用。</summary>
    /// <returns>已停止。</returns>
    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationTokenSource run)
    {
        try
        {
            await PumpAsync(run.Token).ConfigureAwait(false);
        }
        finally
        {
            // 清掉句柄，否则下一次断线时 TryStart 会因为"已经在跑"而返回 false，状态就永远停在 Reconnecting。
            lock (sync)
            {
                running = null;
                cancellation = null;
            }

            run.Dispose();
        }
    }

    private async Task PumpAsync(CancellationToken stop)
    {
        for (int attempt = 1; ; attempt++)
        {
            int? delay = backoff.GetDelay(attempt);
            if (delay is null)
            {
                logger.LogWarning("The reconnect loop gave up after {Attempts} attempt(s).", attempt - 1);
                return;
            }

            try
            {
                await Task.Delay(delay.Value, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (await TryOnceAsync(attempt, stop).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>跑一次尝试；成功或这一轮被结束时返回 true。</summary>
    /// <param name="attempt">已尝试的次数（从 1 开始）。</param>
    /// <param name="stop">这一轮的取消令牌。</param>
    /// <returns>不需要再试时为 true。</returns>
    private async Task<bool> TryOnceAsync(int attempt, CancellationToken stop)
    {
        CommResult outcome;
        try
        {
            outcome = await attemptAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception exception)
        {
            // 循环本身不能因为一次意外抛错就退出；下一次退避之后继续（P61-D9）。
            logger.LogWarning(exception, "Reconnect attempt {Attempt} failed unexpectedly.", attempt);
            return false;
        }

        if (outcome.IsSuccess)
        {
            logger.LogInformation("Reconnected on attempt {Attempt}.", attempt);
            return true;
        }

        // 认证失败也继续重试：Broker 侧的凭据可能已经被修正（P61-D9）。
        onFailure(attempt, outcome);
        return false;
    }
}
