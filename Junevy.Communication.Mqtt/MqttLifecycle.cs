using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Sdk;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 连接、断开与释放（P61-D4、D10、D11）。与发布、订阅分开，是因为它们的并发规则不同：这里的操作全部经过同一把生命周期锁，
/// SDK 不允许连接、断开、释放彼此并发；<see cref="MqttMessaging"/> 的操作不经过它。
/// </summary>
internal sealed class MqttLifecycle
{
    private readonly MqttSdkClient sdk;
    private readonly MqttConnectionStateMachine state;
    private readonly MqttReconnectController reconnect;
    private readonly MqttLifecycleGate gate;

    // 释放时取消正在进行的首次连接，让释放不必等到连接超时。不释放它：它没有计时器，也从不访问 WaitHandle，
    // 而释放之后仍可能有刚越过释放检查的 ConnectAsync 要读它的令牌。
    private readonly CancellationTokenSource lifetime = new();
    private readonly string endpoint;
    private readonly ILogger logger;
    private int disposed;

    public MqttLifecycle(
        MqttSdkClient sdk,
        MqttConnectionStateMachine state,
        MqttReconnectController reconnect,
        MqttLifecycleGate gate,
        string endpoint,
        ILogger logger)
    {
        this.sdk = sdk;
        this.state = state;
        this.reconnect = reconnect;
        this.gate = gate;
        this.endpoint = endpoint;
        this.logger = logger;
    }

    /// <summary>是否已经释放（或正在释放）。</summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>释放之后的发布、订阅、退订与被释放打断的连接都返回这个结果（P6-C8）。</summary>
    /// <returns><see cref="CommErrorKind.ConnectionClosed"/> 的结果。</returns>
    public static CommResult Disposed() => CommResult.Fail("The MQTT client has been disposed.", CommErrorKind.ConnectionClosed);

    /// <summary>连接 Broker；语义见 <see cref="MqttClient.ConnectAsync"/>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果。</returns>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken)
    {
        // 先看状态再看锁：已连接时不需要排队（P61-D4）。
        if (state.Current == MqttConnectionState.Connected)
        {
            return CommResult.Success();
        }

        if (state.Current is MqttConnectionState.Connecting or MqttConnectionState.Reconnecting)
        {
            return InProgress();
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        IAsyncDisposable lease;
        try
        {
            lease = await gate.EnterAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 排队等锁时被取消：调用方取消返回 Cancelled，释放导致的取消返回 ConnectionClosed（P6-C8）。
            return IsDisposed ? Disposed() : CommResult.Fail("The connection attempt was cancelled.", CommErrorKind.Cancelled);
        }

        await using (lease.ConfigureAwait(false))
        {
            return await ConnectUnderLockAsync(linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>主动断开；语义见 <see cref="MqttClient.DisconnectAsync"/>。</summary>
    /// <param name="cancellationToken">取消令牌；被触发时抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>断开完成。</returns>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        // 先结束正在进行的那一轮重连：它可能正握着锁在连接，取消之后锁很快就会空出来。
        await reconnect.StopAsync().ConfigureAwait(false);

        await using IAsyncDisposable lease = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);

        // 锁内先改状态：此后 SDK 报来的断线不会再启动重连（只有 Connected → Reconnecting 那一步会启动）。
        // 拿锁之前那一刻可能又启动了一轮，因此再停一次；它若正在等锁会被取消，不会死锁（P61-D9、P61-D10）。
        state.MoveTo(MqttConnectionState.Disconnected, "The client was disconnected by the application.");
        await reconnect.StopAsync().ConfigureAwait(false);

        CommResult outcome = await sdk.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            logger.LogWarning("The MQTT client was not disconnected cleanly: {Outcome}.", outcome);
        }
    }

    /// <summary>释放；语义见 <see cref="MqttClient.DisposeAsync"/>。可重复调用。</summary>
    /// <returns>释放完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // 先取消正在进行的首次连接，再永久停止重连并等它退出：释放之后不留残留的后台任务（Q40、P61-D11）。
        lifetime.Cancel();
        await reconnect.DisposeAsync().ConfigureAwait(false);

        // SDK 不允许释放与连接、断开并发，因此等正在进行的生命周期操作走完（已被取消，或受 Timeout 约束）。
        await using (await gate.EnterAsync(CancellationToken.None).ConfigureAwait(false))
        {
            state.MoveTo(MqttConnectionState.Disconnected, "The client was disposed.");
            await sdk.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<CommResult> ConnectUnderLockAsync(CancellationToken cancellationToken)
    {
        // 排队期间状态可能已经变了，客户端也可能已被释放，因此进锁后再判一次（P61-D10）。
        if (IsDisposed)
        {
            return Disposed();
        }

        if (state.Current == MqttConnectionState.Connected)
        {
            return CommResult.Success();
        }

        if (state.Current is MqttConnectionState.Connecting or MqttConnectionState.Reconnecting)
        {
            return InProgress();
        }

        state.MoveTo(MqttConnectionState.Connecting, null);
        Stopwatch timer = Stopwatch.StartNew();

        // 首次连接失败不启动后台重连（P6-C11）；首次失败由调用方决定要不要重试。
        (CommResult outcome, bool _) = await sdk.ConnectAsync(cancellationToken).ConfigureAwait(false);
        timer.Stop();

        if (IsDisposed)
        {
            outcome = Disposed();
        }
        else if (outcome.IsSuccess && !sdk.IsConnected)
        {
            // 连上之后、报告 Connected 之前又断了：SDK 的断线事件因为状态还是 Connecting 而被忽略，因此在这里补查。
            outcome = CommResult.Fail("The connection was lost right after it was established.", CommErrorKind.ConnectionClosed);
        }

        state.MoveTo(outcome.IsSuccess ? MqttConnectionState.Connected : MqttConnectionState.Disconnected, outcome.ErrorMessage);
        if (outcome.IsSuccess)
        {
            logger.LogInformation("Connected to the MQTT broker at {Endpoint} in {Elapsed} ms.", endpoint, timer.ElapsedMilliseconds);
        }
        else
        {
            logger.LogWarning("The MQTT connection attempt failed after {Elapsed} ms: {Outcome}.", timer.ElapsedMilliseconds, outcome);
        }

        return outcome;
    }

    private static CommResult InProgress() => CommResult.Fail(
        "A connection attempt is already in progress.",
        CommErrorKind.InvalidRequest);
}
