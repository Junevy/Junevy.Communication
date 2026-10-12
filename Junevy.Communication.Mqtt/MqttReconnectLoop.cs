using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 后台重连循环（P61-D9）。**不是**通用的"重试一个操作"，因此不放在 Core：它带状态迁移与恢复订阅。
/// <para>退避用 Core 的 <see cref="ExponentialBackoff"/>，不限次数，每次尝试前先等待。
/// 不在 SDK 的事件回调里发起重连——MQTTnet 明确警告这样会死锁，因此由 <see cref="MqttClient"/> 在事件回调里
/// 只是"启动"这个循环，循环本身跑在自己的任务上。</para>
/// </summary>
internal sealed class MqttReconnectLoop : IAsyncDisposable
{
    private readonly IBackoffPolicy backoff;
    private readonly Func<CancellationToken, Task<CommResult>> attemptAsync;
    private readonly Action<int, CommResult> onFailure;
    private readonly ILogger logger;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object sync = new();
    private Task? running;
    private int stopping;

    /// <summary>创建重连循环。</summary>
    /// <param name="backoff">退避策略；返回 null 表示放弃。</param>
    /// <param name="attemptAsync">一次连接尝试；成功即表示重连完成。</param>
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

    /// <summary>启动循环。已经在跑、或者已经被停掉时返回 false。</summary>
    /// <returns>本次调用真正启动了循环时为 true。</returns>
    public bool TryStart()
    {
        lock (sync)
        {
            if (running is not null || Volatile.Read(ref stopping) != 0)
            {
                return false;
            }

            running = Task.Run(RunAsync);
            return true;
        }
    }

    /// <summary>停止循环并等待它退出。停掉之后不能再次启动。</summary>
    /// <returns>循环已退出。</returns>
    public async Task StopAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        shutdown.Cancel();

        Task? pending;
        lock (sync)
        {
            pending = running;
        }

        if (pending is null)
        {
            return;
        }

        // 取消会让正在等锁或正在连接的那次尝试抛 OperationCanceledException，循环随即退出。
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止导致的取消是预期结果。
        }
    }

    /// <summary>停止循环并等待它退出。可重复调用。</summary>
    /// <returns>循环已退出。</returns>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await PumpAsync().ConfigureAwait(false);
        }
        finally
        {
            // 清掉句柄，否则下一次断线时 TryStart 会因为"已经在跑"而返回 false，状态就永远停在 Reconnecting。
            lock (sync)
            {
                running = null;
            }
        }
    }

    private async Task PumpAsync()
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
                await Task.Delay(delay.Value, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (await TryOnceAsync(attempt).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>跑一次尝试，成功返回 true。</summary>
    /// <param name="attempt">已尝试的次数（从 1 开始）。</param>
    /// <returns>连接成功时为 true。</returns>
    private async Task<bool> TryOnceAsync(int attempt)
    {
        CommResult outcome;
        try
        {
            outcome = await attemptAsync(shutdown.Token).ConfigureAwait(false);
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